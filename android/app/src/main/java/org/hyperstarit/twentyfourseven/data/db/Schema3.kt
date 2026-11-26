package org.hyperstarit.twentyfourseven.data.db

/**
 * Version 3 keys every synced row by (accountId, id): remote ids are only unique on their own server.
 * Each table is rebuilt from its version-2 rows (overrides learn their account from their series);
 * nothing the user made is lost. With more than one account, rows may already have been mixed up
 * under version 2: every account then pulls a full listing again, which puts each server's copy back.
 */
internal val MIGRATION_2_3_SQL: List<String> = buildList {
    // calendars
    add("CREATE TABLE IF NOT EXISTS `new_calendars` (`id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `name` TEXT NOT NULL, `color` TEXT, `timeZone` TEXT, `defaultCategoryId` TEXT, `defaultReminders` TEXT NOT NULL, `readOnly` INTEGER NOT NULL, `sortOrder` INTEGER NOT NULL, `version` TEXT NOT NULL, PRIMARY KEY(`accountId`, `id`))")
    add("INSERT INTO `new_calendars` (`id`, `accountId`, `name`, `color`, `timeZone`, `defaultCategoryId`, `defaultReminders`, `readOnly`, `sortOrder`, `version`) SELECT `id`, `accountId`, `name`, `color`, `timeZone`, `defaultCategoryId`, `defaultReminders`, `readOnly`, `sortOrder`, `version` FROM `calendars`")
    // categories
    add("CREATE TABLE IF NOT EXISTS `new_categories` (`id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `name` TEXT NOT NULL, `color` TEXT NOT NULL, `version` TEXT NOT NULL, PRIMARY KEY(`accountId`, `id`))")
    add("INSERT INTO `new_categories` (`id`, `accountId`, `name`, `color`, `version`) SELECT `id`, `accountId`, `name`, `color`, `version` FROM `categories`")
    // events
    add("CREATE TABLE IF NOT EXISTS `new_events` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `calendarId` TEXT NOT NULL, `title` TEXT NOT NULL, `description` TEXT, `location` TEXT, `categoryId` TEXT, `color` TEXT, `start` INTEGER NOT NULL, `end` INTEGER, `allDay` INTEGER NOT NULL, `timeZone` TEXT, `recurrence` TEXT, `exDates` TEXT NOT NULL, `reminders` TEXT NOT NULL, `attendees` TEXT NOT NULL, `invitationStatus` TEXT, `organizerEmail` TEXT, `version` TEXT, PRIMARY KEY(`accountId`, `id`))")
    add("INSERT INTO `new_events` (`localDeleted`, `id`, `accountId`, `calendarId`, `title`, `description`, `location`, `categoryId`, `color`, `start`, `end`, `allDay`, `timeZone`, `recurrence`, `exDates`, `reminders`, `attendees`, `invitationStatus`, `organizerEmail`, `version`) SELECT `localDeleted`, `id`, `accountId`, `calendarId`, `title`, `description`, `location`, `categoryId`, `color`, `start`, `end`, `allDay`, `timeZone`, `recurrence`, `exDates`, `reminders`, `attendees`, `invitationStatus`, `organizerEmail`, `version` FROM `events`")
    // event_edits
    add("CREATE TABLE IF NOT EXISTS `new_event_edits` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `calendarId` TEXT NOT NULL, `title` TEXT NOT NULL, `description` TEXT, `location` TEXT, `categoryId` TEXT, `color` TEXT, `start` INTEGER NOT NULL, `end` INTEGER, `allDay` INTEGER NOT NULL, `timeZone` TEXT, `recurrence` TEXT, `exDates` TEXT NOT NULL, `reminders` TEXT NOT NULL, `attendees` TEXT NOT NULL, `invitationStatus` TEXT, `organizerEmail` TEXT, `version` TEXT, PRIMARY KEY(`accountId`, `id`))")
    add("INSERT INTO `new_event_edits` (`localDeleted`, `id`, `accountId`, `calendarId`, `title`, `description`, `location`, `categoryId`, `color`, `start`, `end`, `allDay`, `timeZone`, `recurrence`, `exDates`, `reminders`, `attendees`, `invitationStatus`, `organizerEmail`, `version`) SELECT `localDeleted`, `id`, `accountId`, `calendarId`, `title`, `description`, `location`, `categoryId`, `color`, `start`, `end`, `allDay`, `timeZone`, `recurrence`, `exDates`, `reminders`, `attendees`, `invitationStatus`, `organizerEmail`, `version` FROM `event_edits`")
    // tasks
    add("CREATE TABLE IF NOT EXISTS `new_tasks` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `calendarId` TEXT NOT NULL, `title` TEXT NOT NULL, `notes` TEXT, `due` INTEGER, `dueAllDay` INTEGER NOT NULL, `timeZone` TEXT, `status` TEXT NOT NULL, `completedAt` INTEGER, `priority` TEXT, `parentId` TEXT, `sortOrder` INTEGER NOT NULL, `reminders` TEXT NOT NULL, `version` TEXT, PRIMARY KEY(`accountId`, `id`))")
    add("INSERT INTO `new_tasks` (`localDeleted`, `id`, `accountId`, `calendarId`, `title`, `notes`, `due`, `dueAllDay`, `timeZone`, `status`, `completedAt`, `priority`, `parentId`, `sortOrder`, `reminders`, `version`) SELECT `localDeleted`, `id`, `accountId`, `calendarId`, `title`, `notes`, `due`, `dueAllDay`, `timeZone`, `status`, `completedAt`, `priority`, `parentId`, `sortOrder`, `reminders`, `version` FROM `tasks`")
    // task_edits
    add("CREATE TABLE IF NOT EXISTS `new_task_edits` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `calendarId` TEXT NOT NULL, `title` TEXT NOT NULL, `notes` TEXT, `due` INTEGER, `dueAllDay` INTEGER NOT NULL, `timeZone` TEXT, `status` TEXT NOT NULL, `completedAt` INTEGER, `priority` TEXT, `parentId` TEXT, `sortOrder` INTEGER NOT NULL, `reminders` TEXT NOT NULL, `version` TEXT, PRIMARY KEY(`accountId`, `id`))")
    add("INSERT INTO `new_task_edits` (`localDeleted`, `id`, `accountId`, `calendarId`, `title`, `notes`, `due`, `dueAllDay`, `timeZone`, `status`, `completedAt`, `priority`, `parentId`, `sortOrder`, `reminders`, `version`) SELECT `localDeleted`, `id`, `accountId`, `calendarId`, `title`, `notes`, `due`, `dueAllDay`, `timeZone`, `status`, `completedAt`, `priority`, `parentId`, `sortOrder`, `reminders`, `version` FROM `task_edits`")
    // overrides: the account of its series (an override whose series is gone was never shown)
    add("CREATE TABLE IF NOT EXISTS `new_overrides` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `seriesId` TEXT NOT NULL, `recurrenceId` INTEGER NOT NULL, `title` TEXT NOT NULL, `description` TEXT, `location` TEXT, `categoryId` TEXT, `start` INTEGER NOT NULL, `end` INTEGER, `allDay` INTEGER NOT NULL, `timeZone` TEXT, `reminders` TEXT NOT NULL, PRIMARY KEY(`accountId`, `id`))")
    add(
        "INSERT INTO `new_overrides` (`localDeleted`, `id`, `accountId`, `seriesId`, `recurrenceId`, `title`, `description`, `location`, `categoryId`, `start`, `end`, `allDay`, `timeZone`, `reminders`) SELECT * FROM (SELECT o.`localDeleted`, o.`id`, (SELECT COALESCE((SELECT f.accountId FROM `events` f WHERE f.id = o.seriesId), (SELECT s.accountId FROM `event_edits` s WHERE s.id = o.seriesId))) AS `accountId`, o.`seriesId`, o.`recurrenceId`, o.`title`, o.`description`, o.`location`, o.`categoryId`, o.`start`, o.`end`, o.`allDay`, o.`timeZone`, o.`reminders` FROM `overrides` o) WHERE `accountId` IS NOT NULL",
    )
    // override_edits: the account of its series (an override whose series is gone was never shown)
    add("CREATE TABLE IF NOT EXISTS `new_override_edits` (`localDeleted` INTEGER NOT NULL, `id` TEXT NOT NULL, `accountId` TEXT NOT NULL, `seriesId` TEXT NOT NULL, `recurrenceId` INTEGER NOT NULL, `title` TEXT NOT NULL, `description` TEXT, `location` TEXT, `categoryId` TEXT, `start` INTEGER NOT NULL, `end` INTEGER, `allDay` INTEGER NOT NULL, `timeZone` TEXT, `reminders` TEXT NOT NULL, PRIMARY KEY(`accountId`, `id`))")
    add(
        "INSERT INTO `new_override_edits` (`localDeleted`, `id`, `accountId`, `seriesId`, `recurrenceId`, `title`, `description`, `location`, `categoryId`, `start`, `end`, `allDay`, `timeZone`, `reminders`) SELECT * FROM (SELECT o.`localDeleted`, o.`id`, (SELECT COALESCE((SELECT f.accountId FROM `event_edits` f WHERE f.id = o.seriesId), (SELECT s.accountId FROM `events` s WHERE s.id = o.seriesId))) AS `accountId`, o.`seriesId`, o.`recurrenceId`, o.`title`, o.`description`, o.`location`, o.`categoryId`, o.`start`, o.`end`, o.`allDay`, o.`timeZone`, o.`reminders` FROM `override_edits` o) WHERE `accountId` IS NOT NULL",
    )
    add("DROP TABLE `calendars`")
    add("ALTER TABLE `new_calendars` RENAME TO `calendars`")
    add("DROP TABLE `categories`")
    add("ALTER TABLE `new_categories` RENAME TO `categories`")
    add("DROP TABLE `events`")
    add("ALTER TABLE `new_events` RENAME TO `events`")
    add("CREATE INDEX IF NOT EXISTS `index_events_accountId_calendarId` ON `events` (`accountId`, `calendarId`)")
    add("DROP TABLE `event_edits`")
    add("ALTER TABLE `new_event_edits` RENAME TO `event_edits`")
    add("DROP TABLE `overrides`")
    add("ALTER TABLE `new_overrides` RENAME TO `overrides`")
    add("CREATE INDEX IF NOT EXISTS `index_overrides_accountId_seriesId` ON `overrides` (`accountId`, `seriesId`)")
    add("DROP TABLE `override_edits`")
    add("ALTER TABLE `new_override_edits` RENAME TO `override_edits`")
    add("CREATE INDEX IF NOT EXISTS `index_override_edits_accountId_seriesId` ON `override_edits` (`accountId`, `seriesId`)")
    add("DROP TABLE `tasks`")
    add("ALTER TABLE `new_tasks` RENAME TO `tasks`")
    add("CREATE INDEX IF NOT EXISTS `index_tasks_accountId_calendarId` ON `tasks` (`accountId`, `calendarId`)")
    add("DROP TABLE `task_edits`")
    add("ALTER TABLE `new_task_edits` RENAME TO `task_edits`")
    add("DROP INDEX IF EXISTS `index_outbox_accountId`")
    add("DROP INDEX IF EXISTS `index_outbox_entityId`")
    add("CREATE INDEX IF NOT EXISTS `index_outbox_accountId_entityId` ON `outbox` (`accountId`, `entityId`)")
    add("UPDATE accounts SET syncCursor = NULL WHERE (SELECT COUNT(*) FROM accounts) > 1")
}
