package org.hyperstarit.planit.sync

import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest

/** A scripted PlanIT server: pull pages and push answers are set by the test. */
class FakeServer {
    val server = MockWebServer()
    val pushes = CopyOnWriteArrayList<JsonObject>()
    val pullCursors = CopyOnWriteArrayList<String?>()
    val logins = AtomicInteger()
    val refreshes = AtomicInteger()
    var refreshRefused = false
    var accessValidFor = 900L

    /** Pull pages handed out in order (the last one repeats). */
    var pages: MutableList<String> = mutableListOf(page())

    /** Answers a push: each op (as JSON) → its result JSON. */
    var pushAnswer: (JsonObject) -> String = { op -> """{"opId":"${op["opId"]!!.jsonPrimitive.content}","status":"ok"}""" }

    private val json = Json

    init {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.url.encodedPath) {
                "/api/auth/login" -> { logins.incrementAndGet(); tokens() }
                "/api/auth/refresh" -> {
                    refreshes.incrementAndGet()
                    if (refreshRefused) response(401, """{"errors":["Session ended."]}""") else tokens()
                }
                "/api/devices/${request.url.pathSegments.last()}" ->
                    response(200, """{"id":"x","name":"n","platform":"android","createdAt":"2026-01-01T00:00:00Z","lastSeenAt":"2026-01-01T00:00:00Z"}""")
                "/api/sync" -> if (request.method == "GET") {
                    pullCursors += request.url.queryParameter("cursor")
                    response(200, if (pages.size > 1) pages.removeAt(0) else pages.first())
                } else {
                    val body = json.parseToJsonElement(request.body!!.utf8()).jsonObject
                    pushes += body
                    val results = body["ops"]!!.jsonArray.joinToString(",") { pushAnswer(it.jsonObject) }
                    response(200, """{"results":[$results]}""")
                }
                else -> response(404, "")
            }
        }
        server.start()
    }

    fun url() = server.url("/")

    private fun tokens() = response(
        200,
        """{"accessToken":"access","accessTokenExpiresAt":"${java.time.Instant.now().plusSeconds(accessValidFor)}",""" +
            """"refreshToken":"refresh","refreshTokenExpiresAt":"${java.time.Instant.now().plusSeconds(86400)}"}""",
    )

    private fun response(code: Int, body: String) = MockResponse.Builder().code(code).body(body).build()

    companion object {
        const val CAL = "11111111-1111-1111-1111-111111111111"

        fun calendar(id: String = CAL, name: String = "Personal") =
            """{"id":"$id","name":"$name","defaultReminders":[],"readOnly":false,"version":"1"}"""

        fun event(id: String, title: String, version: String = "10", overrides: String = "", recurrence: String? = null) =
            """{"id":"$id","calendarId":"$CAL","title":"$title","start":"2026-08-03T07:00:00+00:00","end":"2026-08-03T08:00:00+00:00",""" +
                """"allDay":false,"timeZone":"Europe/Berlin",${recurrence?.let { "\"recurrence\":\"$it\"," } ?: ""}""" +
                """"exDates":[],"reminders":[{"minutesBefore":10,"channel":"WebPush"}],"attendees":[],"overrides":[$overrides],"version":"$version"}"""

        fun task(id: String, title: String, version: String = "20") =
            """{"id":"$id","calendarId":"$CAL","title":"$title","dueAllDay":false,"status":"NeedsAction","sortOrder":0,"reminders":[],"version":"$version"}"""

        fun page(
            events: List<String> = emptyList(), tasks: List<String> = emptyList(), deletedEvents: List<String> = emptyList(),
            calendars: List<String> = listOf(calendar()), reset: Boolean = false, hasMore: Boolean = false, cursor: String = "c1",
        ) = """{"protocol":1,"reset":$reset,"cursor":"$cursor","hasMore":$hasMore,"calendars":[${calendars.joinToString(",")}],""" +
            """"categories":[],"events":[${events.joinToString(",")}],"tasks":[${tasks.joinToString(",")}],""" +
            """"deleted":{"events":[${deletedEvents.joinToString(",") { "\"$it\"" }}],"tasks":[]}}"""
    }
}
