package org.hyperstarit.planit.data.db

/** The version-2 tables exactly as Room creates them (from app/schemas/…/2.json), for MIGRATION_1_2. */
internal val V2_SCHEMA: List<String> = listOf(
    "CREATE TABLE IF NOT EXISTS `accounts` (`id` TEXT NOT NULL, `kind` TEXT NOT NULL, `server` TEXT NOT NULL, `username` TEXT NOT NULL, `certificatePin` TEXT, `syncCursor` TEXT, `lastSyncAt` INTEGER, `lastError` TEXT, `needsSignIn` INTEGER NOT NULL, `certificateChanged` INTEGER NOT NULL, `createdAt` INTEGER NOT NULL, PRIMARY KEY(`id`))",
    "CREATE TABLE IF NOT EXISTS `calendars` (`id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `name` TEXT NOT NULL, `color` TEXT, `timeZone` TEXT, `defaultCategoryId` TEXT, `defaultReminders` TEXT NOT NULL, `readOnly` INTEGER NOT NULL, `sortOrder` INTEGER NOT NULL, `version` TEXT NOT NULL, PRIMARY KEY(`id`))",
    "CREATE INDEX IF NOT EXISTS `index_calendars_accountId` ON `calendars` (`accountId`)",
    "CREATE TABLE IF NOT EXISTS `categories` (`id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `name` TEXT NOT NULL, `color` TEXT NOT NULL, `version` TEXT NOT NULL, PRIMARY KEY(`id`))",
    "CREATE INDEX IF NOT EXISTS `index_categories_accountId` ON `categories` (`accountId`)",
    "CREATE TABLE IF NOT EXISTS `events` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `calendarId` TEXT NOT NULL, `title` TEXT NOT NULL, `description` TEXT, `location` TEXT, `categoryId` TEXT, `color` TEXT, `start` INTEGER NOT NULL, `end` INTEGER, `allDay` INTEGER NOT NULL, `timeZone` TEXT, `recurrence` TEXT, `exDates` TEXT NOT NULL, `reminders` TEXT NOT NULL, `attendees` TEXT NOT NULL, `invitationStatus` TEXT, `organizerEmail` TEXT, `version` TEXT, PRIMARY KEY(`id`))",
    "CREATE INDEX IF NOT EXISTS `index_events_accountId` ON `events` (`accountId`)",
    "CREATE INDEX IF NOT EXISTS `index_events_calendarId` ON `events` (`calendarId`)",
    "CREATE TABLE IF NOT EXISTS `event_edits` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `calendarId` TEXT NOT NULL, `title` TEXT NOT NULL, `description` TEXT, `location` TEXT, `categoryId` TEXT, `color` TEXT, `start` INTEGER NOT NULL, `end` INTEGER, `allDay` INTEGER NOT NULL, `timeZone` TEXT, `recurrence` TEXT, `exDates` TEXT NOT NULL, `reminders` TEXT NOT NULL, `attendees` TEXT NOT NULL, `invitationStatus` TEXT, `organizerEmail` TEXT, `version` TEXT, PRIMARY KEY(`id`))",
    "CREATE INDEX IF NOT EXISTS `index_event_edits_accountId` ON `event_edits` (`accountId`)",
    "CREATE TABLE IF NOT EXISTS `overrides` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `seriesId` TEXT NOT NULL, `recurrenceId` INTEGER NOT NULL, `title` TEXT NOT NULL, `description` TEXT, `location` TEXT, `categoryId` TEXT, `start` INTEGER NOT NULL, `end` INTEGER, `allDay` INTEGER NOT NULL, `timeZone` TEXT, `reminders` TEXT NOT NULL, PRIMARY KEY(`id`))",
    "CREATE INDEX IF NOT EXISTS `index_overrides_seriesId` ON `overrides` (`seriesId`)",
    "CREATE TABLE IF NOT EXISTS `override_edits` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `seriesId` TEXT NOT NULL, `recurrenceId` INTEGER NOT NULL, `title` TEXT NOT NULL, `description` TEXT, `location` TEXT, `categoryId` TEXT, `start` INTEGER NOT NULL, `end` INTEGER, `allDay` INTEGER NOT NULL, `timeZone` TEXT, `reminders` TEXT NOT NULL, PRIMARY KEY(`id`))",
    "CREATE INDEX IF NOT EXISTS `index_override_edits_seriesId` ON `override_edits` (`seriesId`)",
    "CREATE TABLE IF NOT EXISTS `tasks` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `calendarId` TEXT NOT NULL, `title` TEXT NOT NULL, `notes` TEXT, `due` INTEGER, `dueAllDay` INTEGER NOT NULL, `timeZone` TEXT, `status` TEXT NOT NULL, `completedAt` INTEGER, `priority` TEXT, `parentId` TEXT, `sortOrder` INTEGER NOT NULL, `reminders` TEXT NOT NULL, `version` TEXT, PRIMARY KEY(`id`))",
    "CREATE INDEX IF NOT EXISTS `index_tasks_accountId` ON `tasks` (`accountId`)",
    "CREATE INDEX IF NOT EXISTS `index_tasks_calendarId` ON `tasks` (`calendarId`)",
    "CREATE TABLE IF NOT EXISTS `task_edits` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `calendarId` TEXT NOT NULL, `title` TEXT NOT NULL, `notes` TEXT, `due` INTEGER, `dueAllDay` INTEGER NOT NULL, `timeZone` TEXT, `status` TEXT NOT NULL, `completedAt` INTEGER, `priority` TEXT, `parentId` TEXT, `sortOrder` INTEGER NOT NULL, `reminders` TEXT NOT NULL, `version` TEXT, PRIMARY KEY(`id`))",
    "CREATE INDEX IF NOT EXISTS `index_task_edits_accountId` ON `task_edits` (`accountId`)",
    "CREATE TABLE IF NOT EXISTS `outbox` (`seq` INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, `accountId` TEXT NOT NULL, `opId` TEXT NOT NULL, `op` TEXT NOT NULL, `entityKind` TEXT NOT NULL, `entityId` TEXT NOT NULL, `baseVersion` TEXT, `occurrence` TEXT, `data` TEXT, `sending` INTEGER NOT NULL, `attempts` INTEGER NOT NULL, `nextAttemptAt` INTEGER NOT NULL, `lastError` TEXT)",
    "CREATE INDEX IF NOT EXISTS `index_outbox_accountId` ON `outbox` (`accountId`)",
    "CREATE INDEX IF NOT EXISTS `index_outbox_entityId` ON `outbox` (`entityId`)",
    "CREATE TABLE IF NOT EXISTS `notices` (`id` INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, `accountId` TEXT NOT NULL, `kind` TEXT NOT NULL, `entityKind` TEXT NOT NULL, `entityId` TEXT NOT NULL, `title` TEXT NOT NULL, `message` TEXT NOT NULL, `op` TEXT, `data` TEXT, `createdAt` INTEGER NOT NULL)",
    "CREATE INDEX IF NOT EXISTS `index_notices_accountId` ON `notices` (`accountId`)",
)
