-- ac_reviews_modules: the per-player module (DLL) review table that
-- Database.AntiCheat.FlagAccountForReview_Module inserts into. It was never in
-- structure.sql, so a database built from it rejected every insert
-- (5131 "FlagAccountForReview_Module failed" lines on 2026-10-14).
CREATE TABLE IF NOT EXISTS `ac_reviews_modules` (
  `report_id` bigint(20) unsigned NOT NULL AUTO_INCREMENT,
  `user_id` bigint(20) unsigned NOT NULL,
  `match_id` bigint(20) unsigned NOT NULL,
  `module_name` varchar(128) NOT NULL,
  `module_path` varchar(512) NOT NULL,
  `module_size` bigint(20) unsigned NOT NULL DEFAULT 0,
  PRIMARY KEY (`report_id`),
  UNIQUE KEY `user_id_match_id_module_name` (`user_id`,`match_id`,`module_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
