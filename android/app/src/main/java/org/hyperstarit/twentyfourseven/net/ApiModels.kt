package org.hyperstarit.twentyfourseven.net

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

// The server's wire format (CalendarIT, camelCase JSON). Sync models follow protocol 1 —
// see CalendarIT.Application/Sync/SyncModels.cs. Instants are ISO 8601 strings with an offset.

@Serializable
data class LoginRequest(val email: String, val password: String)

@Serializable
data class RefreshTokenRequest(val refreshToken: String)

@Serializable
data class AuthTokens(
    val accessToken: String,
    val accessTokenExpiresAt: String,
    val refreshToken: String,
    val refreshTokenExpiresAt: String,
)

@Serializable
data class ErrorList(val errors: List<String> = emptyList())

@Serializable
data class ReminderDto(val minutesBefore: Int, val channel: String)

@Serializable
data class AttendeeDto(val email: String, val name: String? = null, val status: String)

@Serializable
data class SyncPullResponse(
    val protocol: Int,
    val reset: Boolean,
    val cursor: String,
    val hasMore: Boolean,
    val calendars: List<SyncCalendarDto>,
    val categories: List<SyncCategoryDto>,
    val events: List<SyncSeriesDto>,
    val tasks: List<SyncTaskDto>,
    val deleted: SyncDeletions,
)

@Serializable
data class SyncDeletions(val events: List<String>, val tasks: List<String>)

@Serializable
data class SyncCalendarDto(
    val id: String,
    val name: String,
    val color: String? = null,
    val timeZone: String? = null,
    val defaultCategoryId: String? = null,
    val defaultReminders: List<ReminderDto> = emptyList(),
    val readOnly: Boolean,
    val version: String,
    val subscriptionError: String? = null,
)

@Serializable
data class SyncCategoryDto(val id: String, val name: String, val color: String, val version: String)

@Serializable
data class SyncSeriesDto(
    val id: String,
    val calendarId: String,
    val title: String,
    val description: String? = null,
    val location: String? = null,
    val categoryId: String? = null,
    val color: String? = null,
    val start: String,
    val end: String? = null,
    val allDay: Boolean,
    val timeZone: String? = null,
    val recurrence: String? = null,
    val exDates: List<String> = emptyList(),
    val reminders: List<ReminderDto> = emptyList(),
    val attendees: List<AttendeeDto> = emptyList(),
    val invitationStatus: String? = null,
    val organizerEmail: String? = null,
    val overrides: List<SyncOccurrenceDto> = emptyList(),
    val version: String,
)

@Serializable
data class SyncOccurrenceDto(
    val id: String,
    val recurrenceId: String,
    val title: String,
    val description: String? = null,
    val location: String? = null,
    val categoryId: String? = null,
    val start: String,
    val end: String? = null,
    val allDay: Boolean,
    val timeZone: String? = null,
    val reminders: List<ReminderDto> = emptyList(),
)

@Serializable
data class SyncTaskDto(
    val id: String,
    val calendarId: String,
    val title: String,
    val notes: String? = null,
    val due: String? = null,
    val dueAllDay: Boolean,
    val timeZone: String? = null,
    val status: String,
    val completedAt: String? = null,
    val priority: String? = null,
    val parentId: String? = null,
    val sortOrder: Int,
    val reminders: List<ReminderDto> = emptyList(),
    val version: String,
)

@Serializable
data class SyncPushRequest(val ops: List<SyncOp>)

@Serializable
data class SyncOp(
    val opId: String,
    val op: String,
    val id: String,
    val baseVersion: String? = null,
    val occurrence: String? = null,
    val data: JsonElement? = null,
)

@Serializable
data class SyncOpResult(
    val opId: String,
    val status: String,
    val error: String? = null,
    /** On success: the version each series or task the change touched has now. */
    val versions: Map<String, String>? = null,
)

object OpStatus {
    const val OK = "ok"
    const val CONFLICT = "conflict"
    const val NOT_FOUND = "notFound"
    const val INVALID = "invalid"
    const val FORBIDDEN = "forbidden"
    const val ERROR = "error"
}

@Serializable
data class SyncPushResponse(val results: List<SyncOpResult>)

@Serializable
data class RegisterDeviceRequest(
    val name: String,
    val platform: String,
    val appVersion: String? = null,
    val push: DevicePushInput? = null,
)

@Serializable
data class DevicePushInput(val kind: String, val endpoint: String, val p256dh: String? = null, val auth: String? = null)

@Serializable
data class VapidPublicKeyDto(val publicKey: String)

@Serializable
data class DeviceDto(
    val id: String,
    val name: String,
    val platform: String,
    val appVersion: String? = null,
    val pushKind: String? = null,
    val createdAt: String,
    val lastSeenAt: String,
)

/** The signed-in user's profile (GET /api/profile). Only what the app shows is read. */
@Serializable
data class ProfileDto(
    val email: String? = null,
    /** A `data:image/…;base64,…` URL, or null for no picture. */
    val avatarDataUrl: String? = null,
    val emailConfirmed: Boolean = false,
    val isAdmin: Boolean = false,
)

@Serializable
data class ForgotPasswordRequest(val email: String)

@Serializable
data class ChangePasswordRequest(val currentPassword: String, val newPassword: String)

/** One signed-in device or browser (GET /api/auth/sessions), the current one first. */
@Serializable
data class SessionDto(
    val id: String,
    val userAgent: String? = null,
    val ipAddress: String? = null,
    val startedAt: String,
    val lastActiveAt: String,
    val expiresAt: String,
    val current: Boolean,
)

@Serializable
data class RevokedSessions(val count: Int)
