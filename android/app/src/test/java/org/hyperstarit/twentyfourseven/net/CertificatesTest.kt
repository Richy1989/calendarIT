package org.hyperstarit.twentyfourseven.net

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import java.time.Clock
import javax.net.ssl.SSLException
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.tls.HandshakeCertificates
import okhttp3.tls.HeldCertificate
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.accounts.DeviceInfo
import org.hyperstarit.twentyfourseven.accounts.UntrustedCertificateException
import org.hyperstarit.twentyfourseven.auth.SecretStore
import org.hyperstarit.twentyfourseven.auth.Tokens
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** A home server with its own self-signed certificate. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class CertificatesTest {
    private val held = HeldCertificate.Builder().commonName("calendar.home").addSubjectAlternativeName("localhost").build()
    private val other = HeldCertificate.Builder().commonName("someone.else").addSubjectAlternativeName("localhost").build()
    private val server = MockWebServer()

    private fun serve(cert: HeldCertificate) {
        server.useHttps(HandshakeCertificates.Builder().heldCertificate(cert).build().sslSocketFactory())
        server.dispatcher = object : mockwebserver3.Dispatcher() {
            override fun dispatch(request: mockwebserver3.RecordedRequest) = when (request.url.encodedPath) {
                "/api/auth/login" -> MockResponse.Builder().body(
                    """{"accessToken":"a","accessTokenExpiresAt":"2099-01-01T00:00:00Z","refreshToken":"r","refreshTokenExpiresAt":"2099-01-01T00:00:00Z"}""",
                ).build()
                else -> MockResponse.Builder().code(200).body("{}").build()
            }
        }
        server.start()
    }

    @After
    fun stop() = server.close()

    private fun get(client: OkHttpClient) = client.newCall(Request.Builder().url(server.url("/x")).build()).execute().use { it.code }

    @Test
    fun aSelfSignedCertificate_IsRefused_UntilExactlyItIsPinned() = runBlocking {
        serve(held)
        try {
            get(OkHttpClient())
            fail()
        } catch (e: java.io.IOException) {
            assertTrue(e.isCertificateRefusal())
        }
        val presented = Certificates.probe(server.url("/"))
        assertEquals(Certificates.sha256(held.certificate), presented.sha256)
        assertTrue(presented.selfSigned)

        assertEquals(200, get(Certificates.clientFor(OkHttpClient(), presented.sha256)))
    }

    @Test
    fun anotherCertificate_ThanThePinnedOne_IsRefusedAsChanged() {
        serve(other)
        try {
            get(Certificates.clientFor(OkHttpClient(), Certificates.sha256(held.certificate)))
            fail()
        } catch (e: java.io.IOException) {
            assertTrue(e.isCertificateChange())
        }
    }

    @Test
    fun addingAnAccount_StopsForTheUser_BeforeThePasswordGoesOut() = runBlocking {
        serve(held)
        val db = Room.inMemoryDatabaseBuilder(ApplicationProvider.getApplicationContext(), AppDatabase::class.java).allowMainThreadQueries().build()
        val secrets = object : SecretStore {
            override suspend fun tokens(accountId: String): Tokens? = null
            override suspend fun saveTokens(accountId: String, tokens: Tokens?) {}
            override suspend fun deviceId() = "d"
        }
        val device = object : DeviceInfo {
            override val name = "T"
            override val appVersion = "0"
        }
        val accounts = Accounts(db, secrets, OkHttpClient(), Json { ignoreUnknownKeys = true }, Clock.systemUTC(), device)

        val info = try {
            accounts.addCalendarIt(server.url("/"), "me@test", "pw", pin = null)
            fail()
            return@runBlocking
        } catch (e: UntrustedCertificateException) {
            e.certificate
        }
        assertEquals(0, server.requestCount) // only the handshake ran: no request, no password

        val account = accounts.addCalendarIt(server.url("/"), "me@test", "pw", pin = info.sha256)
        assertEquals(info.sha256, account.certificatePin)
        db.close()
    }
}
