-- Upgrades a database created from the 2026-04-13 structure.sql (commit 16573aa) to the
-- schema expected by upstream 85dfbc7. Safe to re-run: every statement is idempotent on MariaDB.
-- Back up the database before running.

-- match_history: lobby type and client CRCs
ALTER TABLE `match_history`
  ADD COLUMN IF NOT EXISTS `lobby_type` tinyint(3) unsigned DEFAULT NULL AFTER `match_roster_type`,
  ADD COLUMN IF NOT EXISTS `exe_crc` int(10) unsigned NOT NULL DEFAULT 0 AFTER `max_cam_height`,
  ADD COLUMN IF NOT EXISTS `ini_crc` int(10) unsigned NOT NULL DEFAULT 0 AFTER `exe_crc`;

-- users: monthly ELO
ALTER TABLE `users`
  ADD COLUMN IF NOT EXISTS `monthly_elo_rating` int(11) NOT NULL DEFAULT 1000 AFTER `elo_rating`;

-- token refresh / revocation state
CREATE TABLE IF NOT EXISTS `user_token_state` (
  `user_id` bigint(20) NOT NULL,
  `session_type` int(11) NOT NULL,
  `token_generation` int(11) NOT NULL DEFAULT 0,
  `refresh_jti` varchar(64) NOT NULL DEFAULT '',
  `previous_refresh_jti` varchar(64) NOT NULL DEFAULT '',
  `previous_refresh_jti_expires` datetime NOT NULL DEFAULT '1970-01-01 00:00:00',
  `updated` datetime NOT NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`user_id`,`session_type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- external leaderboard publication queue (same as upgrade_20260816_external_leaderboard_publication.sql)
CREATE TABLE IF NOT EXISTS `external_publication` (
  `match_id` bigint(20) NOT NULL,
  `next_attempt_at` datetime DEFAULT NULL,
  `published_at` datetime DEFAULT NULL,
  `attempts` int(11) NOT NULL DEFAULT 0,
  `last_error` varchar(512) DEFAULT NULL,
  PRIMARY KEY (`match_id`),
  KEY `ix_external_publication_pending` (`published_at`,`next_attempt_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- anticheat review tables (mapped in Database/Database.AntiCheat.cs; not in upstream structure.sql)
CREATE TABLE IF NOT EXISTS `ac_reviews_crc` (
  `user_id` bigint(20) NOT NULL,
  `original_crc` varchar(64) NOT NULL,
  `diff_crc` varchar(64) NOT NULL,
  `most_recent_match` bigint(20) unsigned NOT NULL,
  PRIMARY KEY (`user_id`,`original_crc`,`diff_crc`,`most_recent_match`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS `ac_reviews_modules` (
  `report_id` bigint(20) unsigned NOT NULL AUTO_INCREMENT,
  `user_id` bigint(20) unsigned NOT NULL,
  `match_id` bigint(20) unsigned NOT NULL,
  `module_name` varchar(128) NOT NULL,
  `module_path` varchar(512) NOT NULL,
  `module_size` bigint(20) unsigned NOT NULL,
  PRIMARY KEY (`report_id`),
  UNIQUE KEY `user_id_match_id_module_name` (`user_id`,`match_id`,`module_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS `ac_reviews_new_account_games` (
  `report_id` bigint(20) unsigned NOT NULL AUTO_INCREMENT,
  `user_id` bigint(20) unsigned NOT NULL,
  `match_id` bigint(20) unsigned NOT NULL,
  PRIMARY KEY (`report_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

CREATE TABLE IF NOT EXISTS `ac_reviews_probes` (
  `report_id` bigint(20) unsigned NOT NULL AUTO_INCREMENT,
  `user_id` bigint(20) unsigned NOT NULL,
  `match_id` bigint(20) unsigned NOT NULL,
  `reason` varchar(256) NOT NULL,
  PRIMARY KEY (`report_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- `pending_logins` is no longer used by the service. It is left in place; drop it manually once
-- you no longer need to roll back.
