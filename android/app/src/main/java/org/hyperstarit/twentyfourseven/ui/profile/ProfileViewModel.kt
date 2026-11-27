package org.hyperstarit.twentyfourseven.ui.profile

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.ImageDecoder
import android.net.Uri
import android.util.Base64
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.ByteArrayOutputStream
import java.io.IOException
import javax.inject.Inject
import kotlin.math.max
import kotlin.math.roundToInt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.hyperstarit.twentyfourseven.accounts.Accounts
import org.hyperstarit.twentyfourseven.data.db.AccountEntity
import org.hyperstarit.twentyfourseven.data.db.AppDatabase
import org.hyperstarit.twentyfourseven.net.ApiClient
import org.hyperstarit.twentyfourseven.net.ProfileDto
import org.hyperstarit.twentyfourseven.net.SessionDto

/**
 * One 24seven account's profile as the server keeps it: picture, address, password and the
 * devices signed in to it. Read live from the server (nothing of it is stored on the phone).
 */
@HiltViewModel
class ProfileViewModel @Inject constructor(
    @ApplicationContext private val context: Context,
    private val db: AppDatabase,
    private val accounts: Accounts,
) : ViewModel() {
    var account by mutableStateOf<AccountEntity?>(null)
        private set
    var profile by mutableStateOf<ProfileDto?>(null)
        private set
    var avatar by mutableStateOf<ImageBitmap?>(null)
        private set
    var sessions by mutableStateOf<List<SessionDto>>(emptyList())
        private set
    var loading by mutableStateOf(false)
        private set
    var busy by mutableStateOf(false)
        private set
    /** Why the page couldn't load or an action failed. */
    var error by mutableStateOf<String?>(null)
    /** What just happened, for a friendly line on top. */
    var message by mutableStateOf<String?>(null)

    fun open(accountId: String) {
        if (account?.id == accountId && profile != null) return
        viewModelScope.launch {
            account = db.accounts().get(accountId)
            reload()
        }
    }

    fun reload() {
        val a = account ?: return
        loading = true
        error = null
        viewModelScope.launch {
            try {
                call(a) { api, server, token ->
                    show(api.profile(server, token))
                    sessions = api.sessions(server, token)
                }
            } catch (e: IOException) {
                error = describe(e)
            } finally {
                loading = false
            }
        }
    }

    /** Shrinks the picked image to at most [AVATAR_SIZE] px and uploads it as JPEG. */
    fun uploadAvatar(uri: Uri) = action { a ->
        val bytes = withContext(Dispatchers.IO) { shrink(uri) } ?: throw IOException("That image couldn't be read.")
        call(a) { api, server, token -> show(api.uploadAvatar(server, token, bytes, "image/jpeg")) }
        message = "Picture saved."
    }

    fun removeAvatar() = action { a ->
        call(a) { api, server, token -> api.removeAvatar(server, token) }
        profile = profile?.copy(avatarDataUrl = null)
        avatar = null
        message = "Picture removed."
    }

    /** [done] gets null when the password was changed, else what went wrong. */
    fun changePassword(current: String, new: String, done: (String?) -> Unit) {
        val a = account ?: return
        busy = true
        viewModelScope.launch {
            val failure = try {
                accounts.changePassword(a, current, new)
                null
            } catch (e: IOException) {
                describe(e)
            }
            busy = false
            done(failure)
            if (failure == null) {
                message = "Password changed. Your other devices and browsers were signed out."
                reload()
            }
        }
    }

    fun signOut(session: SessionDto) = action { a ->
        call(a) { api, server, token ->
            api.revokeSession(server, token, session.id)
            sessions = api.sessions(server, token)
        }
    }

    fun signOutOthers() = action { a ->
        call(a) { api, server, token ->
            val n = api.revokeOtherSessions(server, token)
            sessions = api.sessions(server, token)
            message = when (n) {
                0 -> "No other sessions were signed in."
                1 -> "Signed out 1 other session."
                else -> "Signed out $n other sessions."
            }
        }
    }

    private fun action(block: suspend (AccountEntity) -> Unit) {
        val a = account ?: return
        if (busy) return
        busy = true
        error = null
        message = null
        viewModelScope.launch {
            try {
                block(a)
            } catch (e: IOException) {
                error = describe(e)
            } finally {
                busy = false
            }
        }
    }

    private suspend fun <T> call(a: AccountEntity, block: suspend (ApiClient, okhttp3.HttpUrl, String) -> T): T {
        val api = accounts.api(a.certificatePin)
        return accounts.auth(a).authorized { server, token -> block(api, server, token) }
    }

    private fun show(p: ProfileDto) {
        profile = p
        avatar = p.avatarDataUrl?.let(::decodeDataUrl)
    }

    private fun shrink(uri: Uri): ByteArray? = runCatching {
        val source = ImageDecoder.createSource(context.contentResolver, uri)
        val bitmap = ImageDecoder.decodeBitmap(source) { decoder, info, _ ->
            val longest = max(info.size.width, info.size.height)
            if (longest > AVATAR_SIZE) {
                val scale = AVATAR_SIZE.toFloat() / longest
                decoder.setTargetSize((info.size.width * scale).roundToInt().coerceAtLeast(1), (info.size.height * scale).roundToInt().coerceAtLeast(1))
            }
            decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
        }
        ByteArrayOutputStream().use { out ->
            bitmap.compress(Bitmap.CompressFormat.JPEG, 88, out)
            out.toByteArray()
        }
    }.getOrNull()

    companion object {
        const val AVATAR_SIZE = 512

        fun describe(e: IOException): String = e.message?.takeIf { it.isNotBlank() } ?: "Couldn't reach the server."

        fun decodeDataUrl(url: String): ImageBitmap? = runCatching {
            val bytes = Base64.decode(url.substringAfter("base64,", ""), Base64.DEFAULT)
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
        }.getOrNull()
    }
}
