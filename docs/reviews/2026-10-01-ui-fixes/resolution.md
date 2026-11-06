# Review resolutions

Receipt: [review.json](review.json). Reviewed snapshot: `a54597d`, base `7d4a883`.

- Finding1 resolved in `556e70d`: omit removed sources from the settings snapshot, close their tabs after successful persistence with no second write; regression injects a failure on write2.
- Finding2 resolved in `9ce0e4a`: input versions guard late connection-check metadata/status/errors; endpoint/auth/credential edits invalidate status. Both delayed success and failure are checked.
- Pending-save coverage was added to the existing delayed-persistence draft check.
- Final caller verification:249 Release tests passed after the production fixes and acceptance coverage. Relative first requests are bounded against the restart clock; failed settings saves preserve the live tailer, range, rows and query filter.

No actionable production findings remain. Native assistive technology and real-cluster limits remain as recorded in the approved plan. The attempted external peer did not start; six local review lenses and an independent validator completed.
