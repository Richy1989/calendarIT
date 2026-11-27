package org.hyperstarit.twentyfourseven.caldav

import java.io.IOException
import java.io.StringReader
import java.security.MessageDigest
import java.security.SecureRandom
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.Credentials
import okhttp3.HttpUrl
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.xmlpull.v1.XmlPullParser
import org.xmlpull.v1.XmlPullParserFactory

/** The server refused the username or password. */
class DavAuthException : IOException("The server refused the username or password.")

/** The server answered [code] where something else was expected. */
open class DavHttpException(val code: Int, message: String) : IOException(message)

/** If-Match failed: the resource changed on the server since it was read. */
class DavPreconditionException : DavHttpException(412, "It was changed elsewhere first.")

/** The sync token is no longer valid: list the collection again. */
class DavInvalidSyncTokenException : IOException("The sync token expired.")

/** An XML element, as far as WebDAV needs one. */
class XNode(val ns: String, val name: String) {
    val children = mutableListOf<XNode>()
    val attrs = mutableMapOf<String, String>()
    internal val textBuilder = StringBuilder()
    val text: String get() = textBuilder.toString().trim()

    fun child(ns: String, name: String): XNode? = children.firstOrNull { it.ns == ns && it.name == name }

    fun all(ns: String, name: String): List<XNode> = children.filter { it.ns == ns && it.name == name }

    fun has(ns: String, name: String) = child(ns, name) != null

    /** Depth-first search below this node. */
    fun find(ns: String, name: String): XNode? {
        for (c in children) {
            if (c.ns == ns && c.name == name) return c
            c.find(ns, name)?.let { return it }
        }
        return null
    }
}

/** One <response> of a multistatus: its (absolute) URL, its status, and its found properties. */
data class DavResponse(val url: HttpUrl, val status: Int?, val props: XNode)

data class Multistatus(val responses: List<DavResponse>, val syncToken: String?)

data class DavCalendarInfo(
    val url: HttpUrl,
    val name: String?,
    val color: String?,
    val order: Int?,
    val events: Boolean,
    val tasks: Boolean,
    val readOnly: Boolean,
    val ctag: String?,
    val syncToken: String?,
    /** The calendar's zone (CalDAV calendar-timezone), for floating times. */
    val timeZone: String?,
)

data class Discovery(val principal: HttpUrl?, val home: HttpUrl, val addresses: List<String>)

/**
 * A CalDAV server, spoken to as one user. Requests are made by [http] (the account's client, with
 * its certificate pin); the login goes only to the host the account was added with.
 */
class DavClient(base: OkHttpClient, private val host: String, user: String, password: String) {
    private val auth = DavLogin(host, user, password)
    private val http = base.newBuilder().addInterceptor(auth).build()

    // ---------------------------------------------------------------- discovery

    /**
     * Where the user's calendars are: [start] itself, or its /.well-known/caldav, names the
     * principal; the principal names the calendar home and the user's addresses.
     */
    suspend fun discover(start: HttpUrl): Discovery {
        val props = listOf(DAV to "current-user-principal", CALDAV to "calendar-home-set", DAV to "resourcetype")
        var principal: HttpUrl? = null
        var home: HttpUrl? = null
        var startIsCalendar = false
        var startIsCollection = false
        for (url in listOfNotNull(start, start.resolve("/.well-known/caldav"))) {
            val r = runCatching { propfind(url, 0, props).responses.firstOrNull() }.getOrElse { e ->
                if (e is DavAuthException) throw e
                null
            } ?: continue
            if (url == start) {
                startIsCalendar = r.props.child(DAV, "resourcetype")?.has(CALDAV, "calendar") == true
                startIsCollection = r.props.child(DAV, "resourcetype")?.has(DAV, "collection") == true
            }
            principal = r.props.child(DAV, "current-user-principal")?.child(DAV, "href")?.text?.let { r.url.resolve(it) }
            home = r.props.child(CALDAV, "calendar-home-set")?.child(DAV, "href")?.text?.let { r.url.resolve(it) }
            if (principal != null || home != null) break
        }
        var addresses = emptyList<String>()
        if (principal != null) {
            val r = runCatching {
                propfind(principal, 0, listOf(CALDAV to "calendar-home-set", CALDAV to "calendar-user-address-set")).responses.firstOrNull()
            }.getOrElse { e -> if (e is DavAuthException) throw e; null }
            if (r != null) {
                home = r.props.child(CALDAV, "calendar-home-set")?.child(DAV, "href")?.text?.let { r.url.resolve(it) } ?: home
                addresses = r.props.child(CALDAV, "calendar-user-address-set")?.all(DAV, "href").orEmpty()
                    .map { it.text }.filter { it.startsWith("mailto:", ignoreCase = true) }
                    .map { it.substring(7).trim().lowercase() }.filter { it.isNotEmpty() }
            }
        }
        if (home == null) {
            home = when {
                startIsCalendar -> start.resolve("..")
                startIsCollection -> start
                else -> null
            }
        }
        return Discovery(principal, home ?: throw DavHttpException(404, "No calendars found at this address."), addresses.distinct())
    }

    suspend fun calendars(home: HttpUrl): List<DavCalendarInfo> {
        val ms = propfind(
            home, 1,
            listOf(
                DAV to "resourcetype", DAV to "displayname", APPLE to "calendar-color", APPLE to "calendar-order",
                CALDAV to "supported-calendar-component-set", DAV to "current-user-privilege-set",
                CS to "getctag", DAV to "sync-token", CALDAV to "calendar-timezone",
            ),
        )
        return ms.responses.mapNotNull { r ->
            val type = r.props.child(DAV, "resourcetype") ?: return@mapNotNull null
            if (!type.has(CALDAV, "calendar") || r.status?.let { it !in 200..299 } == true) return@mapNotNull null
            val comps = r.props.child(CALDAV, "supported-calendar-component-set")?.all(CALDAV, "comp")
            val names = comps?.mapNotNull { c -> c.attrs["name"]?.uppercase() }
            val privileges = r.props.child(DAV, "current-user-privilege-set")
            val writable = privileges == null || privileges.all(DAV, "privilege").any { p ->
                p.has(DAV, "write") || p.has(DAV, "write-content") || p.has(DAV, "all") || p.has(DAV, "bind")
            }
            DavCalendarInfo(
                url = r.url,
                name = r.props.child(DAV, "displayname")?.text?.takeIf { it.isNotEmpty() },
                color = r.props.child(APPLE, "calendar-color")?.text?.let(::cssColor),
                order = r.props.child(APPLE, "calendar-order")?.text?.toIntOrNull(),
                events = names == null || names.isEmpty() || "VEVENT" in names,
                tasks = names == null || names.isEmpty() || "VTODO" in names,
                readOnly = !writable,
                ctag = r.props.child(CS, "getctag")?.text?.takeIf { it.isNotEmpty() },
                syncToken = r.props.child(DAV, "sync-token")?.text?.takeIf { it.isNotEmpty() },
                timeZone = r.props.child(CALDAV, "calendar-timezone")?.text?.let(::tzidOf),
            )
        }
    }

    // ---------------------------------------------------------------- listing

    /** RFC 6578: what changed since [token] (null = everything). Deleted hrefs come with status 404. */
    suspend fun syncCollection(calendar: HttpUrl, token: String?): Multistatus {
        val body = """<?xml version="1.0" encoding="utf-8"?>""" +
            """<d:sync-collection xmlns:d="DAV:"><d:sync-token>${xml(token.orEmpty())}</d:sync-token>""" +
            """<d:sync-level>1</d:sync-level><d:prop><d:getetag/></d:prop></d:sync-collection>"""
        return try {
            report(calendar, 0, body)
        } catch (e: DavHttpException) {
            if (e.code == 403 || e.code == 409 || e.code == 400) throw DavInvalidSyncTokenException()
            throw e
        }
    }

    /** Every resource's ETag (servers without sync-collection). */
    suspend fun etags(calendar: HttpUrl): Map<HttpUrl, String?> =
        propfind(calendar, 1, listOf(DAV to "getetag", DAV to "resourcetype")).responses
            .filter { it.url != calendar && it.props.child(DAV, "resourcetype")?.has(DAV, "collection") != true }
            .associate { it.url to it.props.child(DAV, "getetag")?.text }

    /** The resources at [urls], with their ETags. */
    suspend fun multiget(calendar: HttpUrl, urls: List<HttpUrl>): List<Pair<DavResponse, String?>> {
        val hrefs = urls.joinToString("") { "<d:href>${xml(it.encodedPath)}</d:href>" }
        val body = """<?xml version="1.0" encoding="utf-8"?>""" +
            """<c:calendar-multiget xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">""" +
            """<d:prop><d:getetag/><c:calendar-data/></d:prop>$hrefs</c:calendar-multiget>"""
        return report(calendar, 1, body).responses.map { it to it.props.child(CALDAV, "calendar-data")?.text }
    }

    // ---------------------------------------------------------------- writing

    /** Writes [ics] at [url]; the new ETag when the server tells it (null = read it again). */
    suspend fun put(url: HttpUrl, ics: String, ifMatch: String?, create: Boolean): String? {
        val request = Request.Builder().url(url).put(ics.toRequestBody(ICS))
            .apply {
                if (create) header("If-None-Match", "*")
                else if (ifMatch != null) header("If-Match", ifMatch)
            }.build()
        return call(request) { r ->
            when {
                r.code == 412 -> throw DavPreconditionException()
                r.isSuccessful -> r.header("ETag")?.takeIf { !it.startsWith("W/") }
                else -> throw failure(r)
            }
        }
    }

    suspend fun delete(url: HttpUrl, ifMatch: String?) {
        val request = Request.Builder().url(url).delete().apply { if (ifMatch != null) header("If-Match", ifMatch) }.build()
        call(request) { r ->
            when {
                r.code == 412 -> throw DavPreconditionException()
                r.code == 404 || r.code == 410 || r.isSuccessful -> Unit
                else -> throw failure(r)
            }
        }
    }

    // ---------------------------------------------------------------- plumbing

    suspend fun propfind(url: HttpUrl, depth: Int, props: List<Pair<String, String>>): Multistatus {
        val prefixes = mapOf(DAV to "d", CALDAV to "c", CS to "cs", APPLE to "a")
        val body = buildString {
            append("""<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="$CALDAV" xmlns:cs="$CS" xmlns:a="$APPLE"><d:prop>""")
            for ((ns, name) in props) append('<').append(prefixes.getValue(ns)).append(':').append(name).append("/>")
            append("</d:prop></d:propfind>")
        }
        return multistatus(url, "PROPFIND", depth, body)
    }

    private suspend fun report(url: HttpUrl, depth: Int, body: String) = multistatus(url, "REPORT", depth, body)

    private suspend fun multistatus(url: HttpUrl, method: String, depth: Int, body: String): Multistatus {
        val request = Request.Builder().url(url).method(method, body.toRequestBody(XML)).header("Depth", depth.toString()).build()
        return call(request) { r ->
            if (r.code != 207) throw failure(r)
            val text = r.body.source().let { src ->
                if (src.request(MAX_RESPONSE + 1)) throw IOException("The server's answer is too large.")
                src.buffer.readUtf8()
            }
            parseMultistatus(r.request.url, text)
        }
    }

    private suspend fun <T> call(request: Request, read: (Response) -> T): T = withContext(Dispatchers.IO) {
        http.newCall(request).execute().use { r ->
            if (r.code == 401) throw DavAuthException()
            read(r)
        }
    }

    private fun failure(r: Response) = DavHttpException(
        r.code,
        when (r.code) {
            403 -> "The server doesn't allow this."
            404, 410 -> "Not found on the server."
            507 -> "The server is out of space (or over your quota)."
            in 500..599 -> "The server had a problem (${r.code})."
            else -> "The server answered ${r.code}."
        },
    )

    companion object {
        const val DAV = "DAV:"
        const val CALDAV = "urn:ietf:params:xml:ns:caldav"
        const val CS = "http://calendarserver.org/ns/"
        const val APPLE = "http://apple.com/ns/ical/"
        private const val MAX_RESPONSE = 32L * 1024 * 1024
        private val XML = "application/xml; charset=utf-8".toMediaType()
        private val ICS = "text/calendar; charset=utf-8".toMediaType()

        fun xml(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;")

        /** "#RRGGBB" or "#RRGGBBAA" (Apple) → "#rrggbb"; anything else → null. */
        fun cssColor(v: String): String? {
            val t = v.trim()
            if (!t.startsWith("#") || (t.length != 7 && t.length != 9) || !t.drop(1).all { it.isDigit() || it.lowercaseChar() in 'a'..'f' }) return null
            return t.take(7).lowercase()
        }

        /** The zone a calendar-timezone (a VCALENDAR with one VTIMEZONE) names. */
        fun tzidOf(text: String): String? =
            ICal.parse(text)?.children("VTIMEZONE")?.firstOrNull()?.prop("TZID")?.value?.let(ICalTime::zoneOf)

        internal fun parseMultistatus(base: HttpUrl, text: String): Multistatus {
            val root = parseXml(text) ?: throw IOException("The server's answer isn't WebDAV.")
            val responses = root.all(DAV, "response").mapNotNull { resp ->
                val href = resp.child(DAV, "href")?.text ?: return@mapNotNull null
                val url = base.resolve(href) ?: return@mapNotNull null
                val props = XNode(DAV, "prop")
                for (ps in resp.all(DAV, "propstat")) {
                    val code = statusCode(ps.child(DAV, "status")?.text)
                    if (code == null || code in 200..299) ps.child(DAV, "prop")?.let { props.children += it.children }
                }
                DavResponse(url, statusCode(resp.child(DAV, "status")?.text), props)
            }
            return Multistatus(responses, root.child(DAV, "sync-token")?.text?.takeIf { it.isNotEmpty() })
        }

        private fun statusCode(line: String?): Int? = line?.trim()?.split(' ')?.getOrNull(1)?.toIntOrNull()

        internal fun parseXml(text: String): XNode? = runCatching {
            val parser = XmlPullParserFactory.newInstance().apply { isNamespaceAware = true }.newPullParser()
            parser.setInput(StringReader(text))
            val stack = ArrayList<XNode>()
            var root: XNode? = null
            while (true) {
                when (parser.next()) {
                    XmlPullParser.START_TAG -> {
                        if (stack.size > 64) return null
                        val n = XNode(parser.namespace.orEmpty(), parser.name)
                        for (i in 0 until parser.attributeCount) n.attrs[parser.getAttributeName(i)] = parser.getAttributeValue(i)
                        if (stack.isEmpty()) root = n else stack.last().children += n
                        stack += n
                    }
                    XmlPullParser.TEXT -> stack.lastOrNull()?.textBuilder?.append(parser.text)
                    XmlPullParser.END_TAG -> stack.removeAt(stack.size - 1)
                    XmlPullParser.END_DOCUMENT -> break
                }
            }
            root
        }.getOrNull()
    }
}

/**
 * Logs in to the account's host — and only that host: a redirect elsewhere gets no credentials.
 * Answers Basic or Digest (Baïkal's default), whichever the server asks for, and keeps answering
 * in advance after the first challenge so most requests need one round trip.
 */
internal class DavLogin(private val host: String, private val user: String, private val password: String) : Interceptor {
    private var scheme: String? = null
    private var digest: Map<String, String> = emptyMap()
    private var nc = 0
    private val random = SecureRandom()

    override fun intercept(chain: Interceptor.Chain): Response {
        val request = chain.request()
        if (!request.url.host.equals(host, ignoreCase = true)) return chain.proceed(request)
        val first = chain.proceed(authorized(request) ?: request)
        if (first.code != 401) return first
        val challenges = first.headers("WWW-Authenticate")
        val chosen = challenges.firstOrNull { it.startsWith("Digest", true) } ?: challenges.firstOrNull { it.startsWith("Basic", true) }
            ?: return first
        synchronized(this) {
            if (chosen.startsWith("Digest", true)) {
                val params = digestParams(chosen.substring(6))
                // A challenge we already answered (not stale) means the password is wrong.
                if (scheme == "digest" && params["stale"]?.equals("true", true) != true && params["nonce"] == digest["nonce"]) return first
                scheme = "digest"
                digest = params
                nc = 0
            } else {
                if (scheme == "basic") return first
                scheme = "basic"
            }
        }
        first.close()
        return chain.proceed(authorized(request) ?: request)
    }

    private fun authorized(request: Request): Request? = synchronized(this) {
        when (scheme) {
            "basic" -> request.newBuilder().header("Authorization", Credentials.basic(user, password, Charsets.UTF_8)).build()
            "digest" -> request.newBuilder().header("Authorization", digestHeader(request)).build()
            else -> null
        }
    }

    private fun digestHeader(request: Request): String {
        val realm = digest["realm"].orEmpty()
        val nonce = digest["nonce"].orEmpty()
        val algorithm = digest["algorithm"] ?: "MD5"
        val hashName = if (algorithm.startsWith("SHA-256", true)) "SHA-256" else "MD5"
        fun h(s: String) = MessageDigest.getInstance(hashName).digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
        val uri = request.url.encodedPath + (request.url.encodedQuery?.let { "?$it" } ?: "")
        val qop = digest["qop"]?.split(',')?.map { it.trim() }?.firstOrNull { it.equals("auth", true) }
        val cnonce = ByteArray(8).also(random::nextBytes).joinToString("") { "%02x".format(it) }
        nc++
        val ncText = "%08x".format(nc)
        var ha1 = h("$user:$realm:$password")
        if (algorithm.endsWith("-sess", true)) ha1 = h("$ha1:$nonce:$cnonce")
        val ha2 = h("${request.method}:$uri")
        val response = if (qop != null) h("$ha1:$nonce:$ncText:$cnonce:$qop:$ha2") else h("$ha1:$nonce:$ha2")
        return buildString {
            append("Digest username=\"").append(user.replace("\"", "")).append("\", realm=\"").append(realm)
            append("\", nonce=\"").append(nonce).append("\", uri=\"").append(uri).append("\", response=\"").append(response).append('"')
            append(", algorithm=").append(algorithm)
            digest["opaque"]?.let { append(", opaque=\"").append(it).append('"') }
            if (qop != null) append(", qop=").append(qop).append(", nc=").append(ncText).append(", cnonce=\"").append(cnonce).append('"')
        }
    }

    private fun digestParams(s: String): Map<String, String> {
        val out = mutableMapOf<String, String>()
        Regex("""(\w+)\s*=\s*(?:"([^"]*)"|([^,\s]+))""").findAll(s).forEach { m ->
            out[m.groupValues[1].lowercase()] = m.groupValues[2].ifEmpty { m.groupValues[3] }
        }
        return out
    }
}
