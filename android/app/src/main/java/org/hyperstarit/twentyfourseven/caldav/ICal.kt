package org.hyperstarit.twentyfourseven.caldav

/**
 * iCalendar (RFC 5545) as a tree of components and property lines, kept as written.
 *
 * CalDAV resources are read and written back by other apps too: a property this app doesn't
 * understand (attachments, X- properties, alarms it can't show, conference links) must survive an
 * edit made here. So a resource is parsed into lines, the few properties the app owns are
 * replaced, and everything else is written back exactly as it came.
 *
 * Outside input: nothing here throws on a malformed line (it is kept or skipped), and nesting is
 * bounded.
 */
class ICalProperty(val name: String, val params: MutableList<Pair<String, String>>, var value: String) {
    fun param(key: String): String? = params.firstOrNull { it.first.equals(key, ignoreCase = true) }?.second

    fun setParam(key: String, v: String?) {
        params.removeAll { it.first.equals(key, ignoreCase = true) }
        if (v != null) params += key.uppercase() to v
    }

    /** The value as text (TEXT values are escaped in the file). */
    val text: String get() = ICal.unescape(value)
}

class ICalComponent(val name: String) {
    val properties = mutableListOf<ICalProperty>()
    val components = mutableListOf<ICalComponent>()

    fun prop(key: String): ICalProperty? = properties.firstOrNull { it.name.equals(key, ignoreCase = true) }

    fun props(key: String): List<ICalProperty> = properties.filter { it.name.equals(key, ignoreCase = true) }

    fun remove(key: String) {
        properties.removeAll { it.name.equals(key, ignoreCase = true) }
    }

    /** Replaces every [key] line with one (keeps the first one's place); null removes it. */
    fun set(key: String, value: String?, params: List<Pair<String, String>> = emptyList()) {
        val at = properties.indexOfFirst { it.name.equals(key, ignoreCase = true) }
        remove(key)
        if (value == null) return
        val line = ICalProperty(key.uppercase(), params.toMutableList(), value)
        if (at >= 0) properties.add(at.coerceAtMost(properties.size), line) else properties += line
    }

    /** A TEXT property, escaped; null or blank removes it. */
    fun setText(key: String, text: String?) = set(key, text?.takeIf { it.isNotEmpty() }?.let(ICal::escape))

    fun children(key: String): List<ICalComponent> = components.filter { it.name.equals(key, ignoreCase = true) }
}

object ICal {
    private const val MAX_DEPTH = 8

    /** The VCALENDAR in [text], or null when there is none. */
    fun parse(text: String): ICalComponent? {
        val stack = ArrayList<ICalComponent>()
        var root: ICalComponent? = null
        for (line in unfold(text)) {
            val p = parseLine(line) ?: continue
            when {
                p.name.equals("BEGIN", true) -> {
                    if (stack.size >= MAX_DEPTH) return null
                    val c = ICalComponent(p.value.trim().uppercase())
                    if (stack.isEmpty()) {
                        if (root != null) break // one calendar per resource
                        root = c
                    } else {
                        stack.last().components += c
                    }
                    stack += c
                }
                p.name.equals("END", true) -> {
                    if (stack.isEmpty()) continue
                    stack.removeAt(stack.size - 1)
                    if (stack.isEmpty()) break
                }
                stack.isNotEmpty() -> stack.last().properties += p
            }
        }
        return root?.takeIf { it.name == "VCALENDAR" }
    }

    fun write(root: ICalComponent): String = StringBuilder().also { writeTo(root, it) }.toString()

    private fun writeTo(c: ICalComponent, out: StringBuilder) {
        out.append("BEGIN:").append(c.name).append("\r\n")
        for (p in c.properties) fold(lineOf(p), out)
        for (child in c.components) writeTo(child, out)
        out.append("END:").append(c.name).append("\r\n")
    }

    private fun lineOf(p: ICalProperty): String = buildString {
        append(p.name)
        for ((k, v) in p.params) {
            append(';').append(k).append('=')
            append(if (v.any { it == ':' || it == ';' || it == ',' } && !v.startsWith('"')) "\"${v.replace("\"", "")}\"" else v)
        }
        append(':').append(p.value.replace("\r", "").replace("\n", "\\n"))
    }

    /** Lines of at most 75 octets, never cutting a UTF-8 character. */
    private fun fold(line: String, out: StringBuilder) {
        var octets = 0
        var i = 0
        while (i < line.length) {
            val cp = line.codePointAt(i)
            val n = Character.charCount(cp)
            val size = when {
                cp < 0x80 -> 1
                cp < 0x800 -> 2
                cp < 0x10000 -> 3
                else -> 4
            }
            if (octets + size > 75) {
                out.append("\r\n ")
                octets = 1
            }
            out.appendCodePoint(cp)
            octets += size
            i += n
        }
        out.append("\r\n")
    }

    private fun unfold(text: String): List<String> {
        val lines = ArrayList<String>()
        for (raw in text.split('\n')) {
            val line = raw.removeSuffix("\r")
            if ((line.startsWith(' ') || line.startsWith('\t')) && lines.isNotEmpty()) {
                lines[lines.size - 1] = lines.last() + line.substring(1)
            } else if (line.isNotEmpty()) {
                lines += line
            }
        }
        return lines
    }

    /** NAME;KEY=VALUE;KEY="QUOTED":VALUE — null for a line that isn't one. */
    internal fun parseLine(line: String): ICalProperty? {
        var i = 0
        var quoted = false
        var colon = -1
        while (i < line.length) {
            val ch = line[i]
            if (ch == '"') quoted = !quoted
            else if (ch == ':' && !quoted) {
                colon = i
                break
            }
            i++
        }
        if (colon <= 0) return null
        val head = line.substring(0, colon)
        val value = line.substring(colon + 1)
        val parts = splitOutsideQuotes(head, ';')
        val name = parts.first().trim()
        if (name.isEmpty() || name.any { !(it.isLetterOrDigit() || it == '-') }) return null
        val params = parts.drop(1).mapNotNull { part ->
            val eq = part.indexOf('=')
            if (eq <= 0) null else part.substring(0, eq).trim().uppercase() to part.substring(eq + 1).trim().removeSurrounding("\"")
        }.toMutableList()
        return ICalProperty(name.uppercase(), params, value)
    }

    private fun splitOutsideQuotes(s: String, sep: Char): List<String> {
        val out = ArrayList<String>()
        val cur = StringBuilder()
        var quoted = false
        for (ch in s) {
            when {
                ch == '"' -> { quoted = !quoted; cur.append(ch) }
                ch == sep && !quoted -> { out += cur.toString(); cur.clear() }
                else -> cur.append(ch)
            }
        }
        out += cur.toString()
        return out
    }

    /** A list value (CATEGORIES, EXDATE): split on commas that aren't escaped. */
    fun splitList(value: String): List<String> {
        val out = ArrayList<String>()
        val cur = StringBuilder()
        var i = 0
        while (i < value.length) {
            val ch = value[i]
            if (ch == '\\' && i + 1 < value.length) {
                cur.append(ch).append(value[i + 1])
                i += 2
                continue
            }
            if (ch == ',') { out += cur.toString(); cur.clear() } else cur.append(ch)
            i++
        }
        out += cur.toString()
        return out.filter { it.isNotEmpty() }
    }

    fun unescape(v: String): String {
        val out = StringBuilder(v.length)
        var i = 0
        while (i < v.length) {
            val ch = v[i]
            if (ch == '\\' && i + 1 < v.length) {
                when (val next = v[i + 1]) {
                    'n', 'N' -> out.append('\n')
                    else -> out.append(next)
                }
                i += 2
            } else {
                out.append(ch)
                i++
            }
        }
        return out.toString()
    }

    fun escape(text: String): String = text
        .replace("\\", "\\\\").replace(";", "\\;").replace(",", "\\,")
        .replace("\r\n", "\n").replace("\r", "\n").replace("\n", "\\n")
}
