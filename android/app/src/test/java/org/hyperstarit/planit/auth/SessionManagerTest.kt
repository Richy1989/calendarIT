package org.hyperstarit.planit.auth

import java.time.Clock
import java.time.Instant
import java.time.ZoneOffset
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import okhttp3.OkHttpClient
import org.hyperstarit.planit.net.ApiClient
import org.hyperstarit.planit.net.UnauthorizedException
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test

class SessionManagerTest {
    private class MemoryStore(var session: Session? = null) : SessionStore {
        override suspend fun load() = session
        override suspend fun save(session: Session?) { this.session = session }
        override suspend fun deviceId() = "device-1"
    }

    private val now = Instant.parse("2026-11-25T12:00:00Z")
    private val clock = Clock.fixed(now, ZoneOffset.UTC)
    private val server = MockWebServer()
    private val refreshes = AtomicInteger()
    private val logins = AtomicInteger()
    private var refreshAnswer: (RecordedRequest) -> MockResponse = { tokens("access-${refreshes.get()}", "refresh-${refreshes.get()}") }
    private var syncAnswer: (RecordedRequest) -> MockResponse = { json(200, "{}") }

    private fun json(code: Int, body: String) = MockResponse.Builder().code(code).body(body).build()

    private fun tokens(access: String, refresh: String, accessIn: Long = 900) = json(
        200,
        """{"accessToken":"$access","accessTokenExpiresAt":"${now.plusSeconds(accessIn)}","refreshToken":"$refresh","refreshTokenExpiresAt":"${now.plusSeconds(86400)}"}""",
    )

    @Before
    fun start() {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.url.encodedPath) {
                "/api/auth/login" -> {
                    logins.incrementAndGet()
                    if (request.body?.utf8()?.contains("wrong") == true) json(401, """{"errors":["Invalid email or password."]}""")
                    else tokens("access-0", "refresh-0")
                }
                "/api/auth/refresh" -> {
                    refreshes.incrementAndGet()
                    Thread.sleep(50) // long enough for concurrent callers to pile up behind the lock
                    refreshAnswer(request)
                }
                "/api/sync" -> syncAnswer(request)
                else -> json(404, "")
            }
        }
        server.start()
    }

    @After
    fun stop() = server.close()

    private fun manager(store: SessionStore = MemoryStore()) =
        SessionManager(ApiClient(OkHttpClient(), Json { ignoreUnknownKeys = true }), store, clock, CoroutineScope(Dispatchers.Default))

    private fun stored(accessIn: Long) = Session(
        server = server.url("/").toString(), email = "me@test", accessToken = "old-access",
        accessExpiresAt = now.epochSecond + accessIn, refreshToken = "old-refresh", refreshExpiresAt = now.epochSecond + 86400,
    )

    @Test
    fun signingIn_KeepsTheSession() = runBlocking {
        val store = MemoryStore()
        val session = manager(store)

        session.login(server.url("/"), " me@test ", "pw")

        assertEquals(AuthState.SignedIn(server.url("/").toString(), "me@test"), session.state.value)
        assertEquals("refresh-0", store.session!!.refreshToken)
    }

    @Test
    fun aRefusedLogin_IsReportedOnce_AndNeverRetried() = runBlocking {
        val session = manager()
        try {
            session.login(server.url("/"), "me@test", "wrong")
            fail()
        } catch (e: UnauthorizedException) {
            assertEquals("Invalid email or password.", e.errors.single())
        }
        assertEquals(1, logins.get())
        assertTrue(session.state.value is AuthState.SignedOut)
    }

    @Test
    fun aFreshToken_IsUsedAsItIs() = runBlocking {
        val session = manager(MemoryStore(stored(accessIn = 600)))
        val token = session.authorized { _, t -> t }
        assertEquals("old-access", token)
        assertEquals(0, refreshes.get())
    }

    @Test
    fun aTokenAboutToRunOut_IsRefreshedOnce_ForAllWhoAskAtOnce() = runBlocking {
        val store = MemoryStore(stored(accessIn = 10))
        val session = manager(store)

        val tokens = (1..8).map { async(Dispatchers.Default) { session.authorized { _, t -> t } } }.awaitAll()

        assertEquals(1, refreshes.get())
        assertEquals(setOf("access-1"), tokens.toSet())
        assertEquals("refresh-1", store.session!!.refreshToken) // the rotated token is kept
    }

    @Test
    fun aRefusedRefresh_EndsTheSession() = runBlocking {
        refreshAnswer = { json(401, """{"errors":["Session ended."]}""") }
        val store = MemoryStore(stored(accessIn = 10))
        val session = manager(store)

        try {
            session.authorized { _, t -> t }
            fail()
        } catch (_: SessionEndedException) {
        }
        assertNull(store.session)
        assertTrue((session.state.value as AuthState.SignedOut).reason != null)
    }

    @Test
    fun aTokenTheServerTurnsDown_IsRefreshedAndTriedOnceMore() = runBlocking {
        val calls = AtomicInteger()
        syncAnswer = { request ->
            calls.incrementAndGet()
            if (request.headers["Authorization"] == "Bearer old-access") json(401, "{}")
            else json(200, """{"protocol":1,"reset":false,"cursor":"c","hasMore":false,"calendars":[],"categories":[],"events":[],"tasks":[],"deleted":{"events":[],"tasks":[]}}""")
        }
        val session = manager(MemoryStore(stored(accessIn = 600)))
        val api = ApiClient(OkHttpClient(), Json { ignoreUnknownKeys = true })

        val page = session.authorized { s, t -> api.pull(s, t, null, 50) }

        assertEquals("c", page.cursor)
        assertEquals(2, calls.get())
        assertEquals(1, refreshes.get())
    }
}
