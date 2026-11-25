package org.hyperstarit.planit.net

import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.serializer
import okhttp3.HttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

/** The server answered, but not with what was asked for. */
open class ApiException(val status: Int, message: String) : IOException(message)

/** 401: wrong login, or a token the server no longer accepts. */
class UnauthorizedException(val errors: List<String>) :
    ApiException(401, errors.firstOrNull() ?: "Not signed in.")

/** The address answers, but it isn't a CalendarIT server (or not one this app can talk to). */
class NotOurServerException : IOException("That doesn't look like a CalendarIT server.")

/**
 * The CalendarIT HTTP API. Stateless: every call names the server, and authorized calls take the
 * access token — [org.hyperstarit.planit.auth.SessionManager] decides which.
 */
class ApiClient(private val http: OkHttpClient, private val json: Json) {

    suspend fun login(server: HttpUrl, email: String, password: String): AuthTokens =
        post(server, "api/auth/login", token = null, LoginRequest(email, password))

    suspend fun refresh(server: HttpUrl, refreshToken: String): AuthTokens =
        post(server, "api/auth/refresh", token = null, RefreshTokenRequest(refreshToken))

    suspend fun logout(server: HttpUrl, refreshToken: String) {
        send<Unit>(request(server, "api/auth/logout", null).post(body(RefreshTokenRequest(refreshToken))), null)
    }

    suspend fun pull(server: HttpUrl, token: String, cursor: String?, limit: Int): SyncPullResponse {
        val url = server.resolve("api/sync")!!.newBuilder()
            .apply { if (cursor != null) addQueryParameter("cursor", cursor) }
            .addQueryParameter("limit", limit.toString())
            .build()
        return send(Request.Builder().url(url).header("Authorization", "Bearer $token").get(), serializer())
    }

    suspend fun push(server: HttpUrl, token: String, ops: List<SyncOp>): SyncPushResponse =
        post(server, "api/sync", token, SyncPushRequest(ops))

    /** The key the server signs its pushes with (VAPID); UnifiedPush distributors check it. */
    suspend fun vapidPublicKey(server: HttpUrl, token: String): String =
        send<VapidPublicKeyDto>(request(server, "api/push/public-key", token).get(), serializer()).publicKey

    suspend fun registerDevice(server: HttpUrl, token: String, deviceId: String, request: RegisterDeviceRequest): DeviceDto =
        send(request(server, "api/devices/$deviceId", token).put(body(request)), serializer())

    private suspend inline fun <reified B, reified T> post(server: HttpUrl, path: String, token: String?, body: B): T =
        send(request(server, path, token).post(body(body)), serializer())

    private fun request(server: HttpUrl, path: String, token: String?): Request.Builder =
        Request.Builder().url(server.resolve(path)!!).apply {
            if (token != null) header("Authorization", "Bearer $token")
        }

    private inline fun <reified B> body(value: B) =
        json.encodeToString(serializer<B>(), value).toRequestBody(JSON)

    /** Runs the request; null [serializer] = no body wanted. */
    private suspend fun <T> send(builder: Request.Builder, serializer: KSerializer<T>?): T =
        withContext(Dispatchers.IO) {
            http.newCall(builder.header("Accept", "application/json").build()).execute().use { response ->
                val text = response.body.string()
                when {
                    response.code == 401 -> throw UnauthorizedException(errors(text))
                    !response.isSuccessful -> throw ApiException(response.code, problem(text) ?: "The server answered ${response.code}.")
                    serializer == null -> @Suppress("UNCHECKED_CAST") (Unit as T)
                    else -> try {
                        json.decodeFromString(serializer, text)
                    } catch (_: SerializationException) {
                        throw NotOurServerException()
                    } catch (_: IllegalArgumentException) {
                        throw NotOurServerException()
                    }
                }
            }
        }

    private fun errors(text: String): List<String> =
        runCatching { json.decodeFromString<ErrorList>(text).errors }.getOrDefault(emptyList())

    /** The title/detail of a ProblemDetails body, when it is one. */
    private fun problem(text: String): String? = runCatching {
        val obj = json.parseToJsonElement(text) as kotlinx.serialization.json.JsonObject
        (obj["detail"] ?: obj["title"])?.let { (it as kotlinx.serialization.json.JsonPrimitive).content }
    }.getOrNull()

    companion object {
        private val JSON = "application/json; charset=utf-8".toMediaType()
    }
}
