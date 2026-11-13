# Evaluation report

Written by `uv run brain eval`, see "Evaluating" in Brain/README.md: every case in `eval/cases/` asked of the seed, `eval/person.json`, at 2026-06-01T12:00:00Z. Do not edit it by hand.

| Case | Group | Recall | Precision | Pass |
| --- | --- | --- | --- | --- |
| aliases-asked-by-an-alias | aliases | 1.00 | 1.00 | yes |
| aliases-history-through-an-alias | aliases | 1.00 | 1.00 | yes |
| another-user-never-in-a-search | another_user | 1.00 | 1.00 | yes |
| another-user-state-never-found | another_user | 1.00 | 1.00 | yes |
| another-user-with-the-same-slug | another_user | 1.00 | 1.00 | yes |
| broader-and-narrower-nothing-from-above | broader_and_narrower | 1.00 | 1.00 | yes |
| broader-and-narrower-records-under-a-topic | broader_and_narrower | 1.00 | 1.00 | yes |
| broader-and-narrower-topics-to-any-depth | broader_and_narrower | 1.00 | 1.00 | yes |
| conflicts-equal-scope-prefers-the-newer | conflicts | 1.00 | 1.00 | yes |
| conflicts-incomparable-scopes-prefer-neither | conflicts | 1.00 | 1.00 | yes |
| conflicts-narrower-scope-wins-whatever-its-age | conflicts | 1.00 | 1.00 | yes |
| history-known-only-to-the-year | history | 1.00 | 1.00 | yes |
| history-of-a-named-thing | history | 1.00 | 1.00 | yes |
| history-of-a-topic-oldest-first | history | 1.00 | 1.00 | yes |
| now-and-stale-confirmed-and-recent-are-current | now_and_stale | 1.00 | 1.00 | yes |
| now-and-stale-flags-what-went-unsupported | now_and_stale | 1.00 | 1.00 | yes |
| now-and-stale-newest-support-first | now_and_stale | 1.00 | 1.00 | yes |
| now-versus-history-a-conflict-does-not-reorder | now_versus_history | 1.00 | 1.00 | yes |
| now-versus-history-nothing-is-stale | now_versus_history | 1.00 | 1.00 | yes |
| profile-asked-about-history | profile | 1.00 | 1.00 | yes |
| profile-who-she-is | profile | 1.00 | 1.00 | yes |
| replaced-decisions-gone-asked-about-now | replaced_decisions | 1.00 | 1.00 | yes |
| replaced-decisions-kept-asked-about-history | replaced_decisions | 1.00 | 1.00 | yes |
| retired-and-edited-a-level-set-three-times | retired_and_edited | 1.00 | 1.00 | yes |
| retired-and-edited-a-retired-state | retired_and_edited | 1.00 | 1.00 | yes |
| retired-and-edited-only-the-newest-version | retired_and_edited | 1.00 | 1.00 | yes |
| retired-and-edited-retired-never-answers | retired_and_edited | 1.00 | 1.00 | yes |
| search-by-kind | search | 1.00 | 1.00 | yes |
| search-leaves-out-older-versions | search | 1.00 | 1.00 | yes |
| search-matches-word-forms | search | 1.00 | 1.00 | yes |
| search-never-finds-a-retired-item | search | 1.00 | 1.00 | yes |
| search-newest-support-first | search | 1.00 | 1.00 | yes |
| search-with-inactive-items | search | 1.00 | 1.00 | yes |
| topics-a-related-topic-is-only-named | topics | 1.00 | 1.00 | yes |
| topics-level-focus-and-history | topics | 1.00 | 1.00 | yes |
| topics-misconceptions-apart-from-history | topics | 1.00 | 1.00 | yes |
| topics-several-in-the-order-asked | topics | 1.00 | 1.00 | yes |

37 cases, 37 pass.

## Answers

What each answer gave, by seed key in its order, a stale item marked, then the conflicts it listed. A case judges only the items it names, so this is where a change it does not judge shows.

- `aliases-asked-by-an-alias`: lv-b-trees, fc-b-trees, m-btree, h-btree-paper; conflicts []
- `aliases-history-through-an-alias`: d-sql, h-index-incident, h-pg-migration; conflicts []
- `another-user-never-in-a-search`: s-mentoring, s-rewrite, p-name, h-joined
- `another-user-state-never-found`: nothing
- `another-user-with-the-same-slug`: lv-databases, h-ta, h-first-db, lv-indexing, m-index, h-index-incident, lv-b-trees, fc-b-trees, m-btree, h-btree-paper; conflicts []
- `broader-and-narrower-nothing-from-above`: lv-indexing, m-index, h-index-incident, lv-b-trees, fc-b-trees, m-btree, h-btree-paper; conflicts []
- `broader-and-narrower-records-under-a-topic`: h-ta, h-first-db, d-idx-plan, d-idx-fk, m-index, h-index-incident, m-btree, h-btree-paper; conflicts [d-idx-fk+d-idx-plan prefer d-idx-plan]
- `broader-and-narrower-topics-to-any-depth`: lv-databases, h-ta, h-first-db, lv-indexing, m-index, h-index-incident, lv-b-trees, fc-b-trees, m-btree, h-btree-paper; conflicts []
- `conflicts-equal-scope-prefers-the-newer`: d-idx-plan, d-idx-fk, m-index, h-index-incident, m-btree, h-btree-paper; conflicts [d-idx-fk+d-idx-plan prefer d-idx-plan]
- `conflicts-incomparable-scopes-prefer-neither`: d-train-course, d-train-club, h-boulder; conflicts [d-train-club+d-train-course prefer None]
- `conflicts-narrower-scope-wins-whatever-its-age`: d-rust-threads, d-rust-async, h-raft; conflicts [d-rust-async+d-rust-threads prefer d-rust-threads]
- `history-known-only-to-the-year`: h-first-db, h-ta; conflicts []
- `history-of-a-named-thing`: h-joined, h-pg-migration, d-orm, d-sql; conflicts []
- `history-of-a-topic-oldest-first`: h-pg-migration, d-orm, h-index-incident, d-sql; conflicts []
- `now-and-stale-confirmed-and-recent-are-current`: s-mentoring, s-competition, s-rewrite, s-course, s-kotlin-book (stale), s-ddia (stale); conflicts []
- `now-and-stale-flags-what-went-unsupported`: s-mentoring, s-competition, s-rewrite, s-course, s-kotlin-book (stale), s-ddia (stale); conflicts []
- `now-and-stale-newest-support-first`: s-mentoring, s-competition, s-rewrite, s-course, s-kotlin-book (stale), s-ddia (stale); conflicts []
- `now-versus-history-a-conflict-does-not-reorder`: d-rust-threads, h-raft, d-rust-async; conflicts [d-rust-async+d-rust-threads prefer d-rust-threads]
- `now-versus-history-nothing-is-stale`: s-mentoring, s-ddia, s-kotlin-book, s-course, s-rewrite, s-competition; conflicts []
- `profile-asked-about-history`: p-name, p-student, p-goal-staff, p-goal-degree-3, p-values, p-interests, p-preferences, p-working, r-examples, r-short, r-sources, r-english, r-no-emoji; conflicts []
- `profile-who-she-is`: p-student, p-name, p-goal-degree-3, p-goal-staff, p-values, p-interests, p-preferences, p-working, r-no-emoji, r-english, r-sources, r-short, r-examples; conflicts []
- `replaced-decisions-gone-asked-about-now`: d-rust-project; conflicts []
- `replaced-decisions-kept-asked-about-history`: d-kotlin-project, d-rust-project; conflicts []
- `retired-and-edited-a-level-set-three-times`: lv-rust-3, fc-rust, h-raft; conflicts []
- `retired-and-edited-a-retired-state`: s-mentoring, s-ddia, s-kotlin-book, s-course, s-rewrite, s-competition; conflicts []
- `retired-and-edited-only-the-newest-version`: p-student, p-name, p-goal-degree-3, p-goal-staff, p-values, p-interests, p-preferences, p-working, r-no-emoji, r-english, r-sources, r-short, r-examples; conflicts []
- `retired-and-edited-retired-never-answers`: p-name, p-student, p-goal-staff, p-goal-degree-3, p-values, p-interests, p-preferences, p-working, r-examples, r-short, r-sources, r-english, r-no-emoji; conflicts []
- `search-by-kind`: h-joined
- `search-leaves-out-older-versions`: p-goal-degree-3
- `search-matches-word-forms`: h-pg-migration
- `search-never-finds-a-retired-item`: nothing
- `search-newest-support-first`: d-idx-plan, d-idx-fk, m-index, h-index-incident, lv-indexing
- `search-with-inactive-items`: p-goal-degree-3, p-goal-degree-2, p-goal-degree
- `topics-a-related-topic-is-only-named`: lv-distributed-systems, fc-distributed-systems; conflicts []
- `topics-level-focus-and-history`: lv-rust-3, fc-rust, h-raft; conflicts []
- `topics-misconceptions-apart-from-history`: lv-consensus, m-consensus, h-raft; conflicts []
- `topics-several-in-the-order-asked`: lv-statistics, fc-statistics, lv-kotlin, fc-kotlin; conflicts []
