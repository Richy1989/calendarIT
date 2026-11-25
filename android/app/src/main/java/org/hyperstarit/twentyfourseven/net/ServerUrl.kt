package org.hyperstarit.twentyfourseven.net

import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * The address of the user's CalendarIT server as typed: "cal.example.com",
 * "https://example.com/calendar" — the API lives under it at `api/`.
 */
object ServerUrl {
    sealed interface Result {
        data class Ok(val url: HttpUrl) : Result
        data class Invalid(val message: String) : Result
    }

    /**
     * A bare host means https. Plain http is accepted only when [allowCleartext] (debug builds,
     * for a dev backend): the password and every token cross this connection.
     */
    fun parse(input: String, allowCleartext: Boolean): Result {
        val text = input.trim()
        if (text.isEmpty()) return Result.Invalid("Enter your server's address.")
        val withScheme = if (text.contains("://")) text else "https://$text"
        val url = withScheme.toHttpUrlOrNull()
            ?: return Result.Invalid("That isn't a web address.")
        if (url.scheme == "http" && !allowCleartext) {
            return Result.Invalid("The server must use https.")
        }
        if (url.username.isNotEmpty() || url.password.isNotEmpty()) {
            return Result.Invalid("Leave your login out of the address.")
        }
        if (url.query != null || url.fragment != null) {
            return Result.Invalid("Use the server's address without anything after ? or #.")
        }
        // Always a directory, so "api/…" resolves beneath it, not beside it.
        val path = url.encodedPath.let { if (it.endsWith("/")) it else "$it/" }
        return Result.Ok(url.newBuilder().encodedPath(path).build())
    }
}
