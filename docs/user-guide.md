# HexTailSharp user guide

## Elastic log sources

An Elastic connection uses separate Kibana and Elasticsearch URLs. Kibana supplies data-view metadata; Elasticsearch supplies read-only log documents. Configure anonymous, Basic, or API-key authentication, then add manual server/namespace source pairs. Checked output fields are joined in order into the existing log stream rows.

Opening a source loads the previous five minutes and polls every two seconds. Source health is checked independently and reports Checking, Connected, Unreachable, Unauthorized, or Misconfigured. Existing searches, context, follow mode, and expanded fields apply to remote tabs.

Passwords and API keys are stored only in the operating-system credential vault and never in `session.json`. Authenticated configuration fails closed when the vault is unavailable. Linux requires an active Secret Service session and `libsecret` (`libsecret-1-dev` on Debian/Ubuntu, `libsecret-devel` on Red Hat, or `libsecret` on Arch).

To smoke-test a native vault, create a temporary authenticated connection, save it, restart HexTailSharp, test the connection, remove it, restart again, and confirm it no longer works. Do not print or copy the credential during the check.

Verification handoff: the save/read/delete round-trip was executed successfully on the current Linux development host. Windows and macOS vault smoke coverage remains outstanding and requires those platforms.
