package org.hyperstarit.twentyfourseven.caldav

import java.security.MessageDigest
import java.util.Base64
import java.util.concurrent.CopyOnWriteArrayList
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import okhttp3.HttpUrl

/**
 * A small CalDAV server in memory: one principal, a calendar home with collections, sync tokens
 * (RFC 6578) and calendar-multiget, ETags with If-Match / If-None-Match. Enough to sync against.
 */
class FakeCalDav(private val user: String = "me", private val password: String = "pw") {
    val server = MockWebServer()
    val requests = CopyOnWriteArrayList<RecordedRequest>()

    var digest = false
    var supportsSync = true
    /** PUT answers without an ETag (as servers do when they changed what was sent). */
    var omitEtagOnPut = false

    class Resource(var ics: String, var etag: String)
    class Collection(val name: String, val components: List<String>, val color: String? = null, val readOnly: Boolean = false) {
        val resources = LinkedHashMap<String, Resource>()
    }

    val collections = LinkedHashMap<String, Collection>()
    private var version = 0
    /** Every change: (version, collection path, resource name, deleted). */
    private val log = ArrayList<Triple<Int, String, Pair<String, Boolean>>>()

    fun url(): HttpUrl = server.url("/")

    @Synchronized
    fun addCalendar(path: String, name: String, vararg components: String, readOnly: Boolean = false) {
        collections[path] = Collection(name, components.toList(), "#FF0000FF", readOnly)
    }

    @Synchronized
    fun put(path: String, name: String, ics: String) {
        version++
        collections.getValue(path).resources[name] = Resource(ics, "\"e$version\"")
        log += Triple(version, path, name to false)
    }

    @Synchronized
    fun remove(path: String, name: String) {
        version++
        collections.getValue(path).resources.remove(name)
        log += Triple(version, path, name to true)
    }

    @Synchronized
    fun ics(path: String, name: String): String? = collections[path]?.resources?.get(name)?.ics

    private val nonce = "n0nce"

    private fun resp(code: Int, body: String, vararg headers: Pair<String, String>): MockResponse =
        MockResponse.Builder().code(code).body(body).apply { headers.forEach { (k, v) -> addHeader(k, v) } }.build()

    init {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                requests += request
                if (!authorized(request)) {
                    val challenge = if (digest) "Digest realm=\"test\", nonce=\"$nonce\", qop=\"auth\", algorithm=MD5" else "Basic realm=\"test\""
                    return resp(401, "", "WWW-Authenticate" to challenge)
                }
                return synchronized(this@FakeCalDav) { handle(request) }
            }
        }
        server.start()
    }

    private fun authorized(r: RecordedRequest): Boolean {
        val h = r.headers["Authorization"] ?: return false
        if (!digest) return h == "Basic " + Base64.getEncoder().encodeToString("$user:$password".toByteArray())
        if (!h.startsWith("Digest ")) return false
        val p = Regex("""(\w+)=(?:"([^"]*)"|([^,\s]+))""").findAll(h).associate { it.groupValues[1] to it.groupValues[2].ifEmpty { it.groupValues[3] } }
        fun md5(s: String) = MessageDigest.getInstance("MD5").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
        val ha1 = md5("$user:test:$password")
        val ha2 = md5("${r.method}:${p["uri"]}")
        return p["response"] == md5("$ha1:$nonce:${p["nc"]}:${p["cnonce"]}:auth:$ha2")
    }

    private fun handle(r: RecordedRequest): MockResponse {
        val path = r.url.encodedPath
        val body = r.body?.utf8().orEmpty()
        return when (r.method) {
            "PROPFIND" -> propfind(path, r.headers["Depth"], body)
            "REPORT" -> report(path, body)
            "PUT" -> putRequest(r, path, body)
            "DELETE" -> delete(r, path)
            else -> resp(405, "")
        }
    }

    private fun ms(inner: String, extra: String = "") = resp(
        207,
        """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/" xmlns:a="http://apple.com/ns/ical/">$inner$extra</d:multistatus>""",
        "Content-Type" to "application/xml; charset=utf-8",
    )

    private fun ok(href: String, props: String) = "<d:response><d:href>$href</d:href><d:propstat><d:prop>$props</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"

    private fun ctag(path: String) = log.lastOrNull { it.second == path }?.first ?: 0

    private fun propfind(path: String, depth: String?, body: String): MockResponse = when {
        path == "/" || path == "/.well-known/caldav" ->
            ms(ok(path, "<d:current-user-principal><d:href>/principals/me/</d:href></d:current-user-principal><d:resourcetype><d:collection/></d:resourcetype>"))
        path == "/principals/me/" ->
            ms(ok(path, "<c:calendar-home-set><d:href>/cal/me/</d:href></c:calendar-home-set><c:calendar-user-address-set><d:href>mailto:me@test</d:href><d:href>/principals/me/</d:href></c:calendar-user-address-set>"))
        path == "/cal/me/" -> ms(
            ok(path, "<d:resourcetype><d:collection/></d:resourcetype>") + collections.entries.joinToString("") { (p, c) ->
                val comps = c.components.joinToString("") { "<c:comp name=\"$it\"/>" }
                val privileges = if (c.readOnly) "<d:privilege><d:read/></d:privilege>" else "<d:privilege><d:read/></d:privilege><d:privilege><d:write/></d:privilege>"
                ok(
                    p,
                    "<d:resourcetype><d:collection/><c:calendar/></d:resourcetype><d:displayname>${c.name}</d:displayname>" +
                        (c.color?.let { "<a:calendar-color>$it</a:calendar-color>" } ?: "") +
                        "<c:supported-calendar-component-set>$comps</c:supported-calendar-component-set>" +
                        "<d:current-user-privilege-set>$privileges</d:current-user-privilege-set>" +
                        "<cs:getctag>c${ctag(p)}</cs:getctag>" +
                        (if (supportsSync) "<d:sync-token>tok-${ctag(p)}</d:sync-token>" else ""),
                )
            },
        )
        collections.containsKey(path) && depth == "1" -> {
            val c = collections.getValue(path)
            ms(ok(path, "<d:resourcetype><d:collection/><c:calendar/></d:resourcetype>") + c.resources.entries.joinToString("") { (n, res) -> ok(path + n, "<d:getetag>${res.etag}</d:getetag><d:resourcetype/>") })
        }
        else -> resp(404, "")
    }

    private fun report(path: String, body: String): MockResponse {
        val c = collections[path] ?: return resp(404, "")
        if (body.contains("sync-collection")) {
            if (!supportsSync) return resp(403, "")
            val token = Regex("<d:sync-token>(.*?)</d:sync-token>").find(body)?.groupValues?.get(1).orEmpty()
            val since = if (token.isEmpty()) -1 else token.removePrefix("tok-").toIntOrNull() ?: return resp(403, "<d:error xmlns:d=\"DAV:\"><d:valid-sync-token/></d:error>")
            val names = if (since < 0) c.resources.keys.map { it to false }
            else log.filter { it.second == path && it.first > since }.map { it.third }.distinctBy { it.first }.map { (n, _) -> n to !c.resources.containsKey(n) }
            val inner = names.joinToString("") { (n, deleted) ->
                if (deleted) "<d:response><d:href>$path$n</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"
                else ok(path + n, "<d:getetag>${c.resources.getValue(n).etag}</d:getetag>")
            }
            return ms(inner, "<d:sync-token>tok-${ctag(path)}</d:sync-token>")
        }
        val hrefs = Regex("<d:href>(.*?)</d:href>").findAll(body).map { it.groupValues[1] }.toList()
        return ms(
            hrefs.joinToString("") { h ->
                val res = c.resources[h.removePrefix(path)]
                if (res == null) "<d:response><d:href>$h</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"
                else ok(h, "<d:getetag>${res.etag}</d:getetag><c:calendar-data>${DavClient.xml(res.ics)}</c:calendar-data>")
            },
        )
    }

    private fun split(path: String): Pair<String, String>? {
        val at = path.lastIndexOf('/')
        val dir = path.substring(0, at + 1)
        return if (collections.containsKey(dir)) dir to path.substring(at + 1) else null
    }

    private fun putRequest(r: RecordedRequest, path: String, body: String): MockResponse {
        val (dir, name) = split(path) ?: return resp(409, "")
        val c = collections.getValue(dir)
        if (c.readOnly) return resp(403, "")
        val existing = c.resources[name]
        if (r.headers["If-None-Match"] == "*" && existing != null) return resp(412, "")
        r.headers["If-Match"]?.let { if (existing == null || existing.etag != it) return resp(412, "") }
        put(dir, name, body)
        val etag = c.resources.getValue(name).etag
        return if (omitEtagOnPut) resp(if (existing == null) 201 else 204, "") else resp(if (existing == null) 201 else 204, "", "ETag" to etag)
    }

    private fun delete(r: RecordedRequest, path: String): MockResponse {
        val (dir, name) = split(path) ?: return resp(404, "")
        val existing = collections.getValue(dir).resources[name] ?: return resp(404, "")
        r.headers["If-Match"]?.let { if (existing.etag != it) return resp(412, "") }
        remove(dir, name)
        return resp(204, "")
    }
}
