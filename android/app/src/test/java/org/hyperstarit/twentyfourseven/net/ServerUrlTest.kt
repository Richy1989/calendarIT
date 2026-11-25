package org.hyperstarit.twentyfourseven.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerUrlTest {
    private fun ok(input: String, cleartext: Boolean = false) =
        (ServerUrl.parse(input, cleartext) as ServerUrl.Result.Ok).url.toString()

    @Test
    fun aBareHost_MeansHttps_AsADirectory() {
        assertEquals("https://cal.example.com/", ok("  cal.example.com "))
        assertEquals("https://example.com/calendar/", ok("https://example.com/calendar"))
        assertEquals("https://example.com/calendar/api/sync", (ServerUrl.parse("example.com/calendar", false) as ServerUrl.Result.Ok).url.resolve("api/sync").toString())
    }

    @Test
    fun plainHttp_OnlyWhenAllowed() {
        assertTrue(ServerUrl.parse("http://10.0.2.2:5299", allowCleartext = false) is ServerUrl.Result.Invalid)
        assertEquals("http://10.0.2.2:5299/", ok("http://10.0.2.2:5299", cleartext = true))
    }

    @Test
    fun loginsQueriesAndJunk_AreRefused() {
        listOf("", "https://me:pw@example.com", "https://example.com/?x=1", "https://example.com/#a", "ht!tp://x")
            .forEach { assertTrue(it, ServerUrl.parse(it, allowCleartext = true) is ServerUrl.Result.Invalid) }
    }
}
