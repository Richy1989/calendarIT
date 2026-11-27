package org.hyperstarit.twentyfourseven.ui.profile

import org.junit.Assert.assertEquals
import org.junit.Test

class DescribeAgentTest {
    @Test
    fun theApp_NamesThePhone() {
        assertEquals(AgentName("24seven on Pixel 8", phone = true), describeAgent("24seven/0.4.0 (Android 15; Pixel 8)"))
    }

    @Test
    fun browsers_AreNamedWithTheirSystem() {
        assertEquals(
            AgentName("Firefox on Linux", phone = false),
            describeAgent("Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0"),
        )
        assertEquals(
            AgentName("Edge on Windows", phone = false),
            describeAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36 Edg/140.0"),
        )
        assertEquals(
            AgentName("Safari on iOS", phone = true),
            describeAgent("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1"),
        )
    }

    @Test
    fun nothingKnown_StillShowsSomething() {
        assertEquals(AgentName("Unknown device", phone = false), describeAgent(null))
        assertEquals(AgentName("curl/8.0", phone = false), describeAgent("curl/8.0"))
    }
}
