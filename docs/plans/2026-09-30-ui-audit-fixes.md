# UI audit fixes

Implement audit findings 1–3 on `audit-ui`, with one fix commit per finding.

1. Replace Elastic results when the range changes. Invalidate queued/in-flight results and cursor updates from the previous range, clear selection and searches' results, retain search definitions. Check with a delayed response regression.
2. Validate both range endpoints against one clock reading; reject reversed, malformed and overflowing ranges. Replace free-text endpoints with native date and time pickers, explicitly labeled UTC, with a live “To now” option. Check parser boundaries and picker interaction.
3. Preserve every saved source, source ID, namespace value and namespace mapping when editing a view. Check a multi-source settings round trip.
4. In a separate UI fix commit, keep placeholder and entered search text centered and visible, and arrange the toolbar to fit the minimum window width. Check all density settings and the 720px window.

Use existing Avalonia controls and test infrastructure. Run the affected checks for each commit and the full suite at the end. Keep commits local; no push was requested.
