# Elastic Servers and Views Design

## Goal

Separate reusable Elastic server credentials/endpoints from the view-specific data-view and filtering configuration, while adding configurable Local/UTC time handling.

## Model

`ElasticConnectionSettings` remains the persisted server record for compatibility with existing session JSON and credential-vault IDs. It gains a `Views` collection. A view is represented by `ElasticViewSettings` and owns its stable ID, display name, selected Kibana data view, time field, filter fields, output fields, and sources.

The old flat data-view properties on a connection are treated as a legacy view during normalization. Existing JSON therefore loads without a converter or credential migration: if `Views` is empty and legacy data-view/source fields exist, normalization creates one view using the connection ID as its stable view ID. New serialization writes the nested view collection and omits the legacy fields from new model construction.

Credentials remain keyed by the server connection ID and are never written to session JSON. Server deletion removes the server credential; editing or deleting a view does not.

## Runtime

Elastic operations receive a server plus view. Data-view discovery uses only the server endpoints/authentication. PIT, search, health checks, and source lookup use the selected view’s data-view and field mappings. Existing Elastic source IDs remain the public tab IDs; they are normalized across nested views and remain stable for migrated sessions.

## Time

`AppSettings.TimeZoneMode` is an enum with `Local` as the default and `Utc` as the alternate. Relative Elastic ranges and diagnostic/display timestamps use the configured zone. Missing JSON defaults to Local.

## UI

The Elastic settings tab renders server editors as outer expanders. Server editors contain endpoint/authentication controls and a list of view editors. Each view is an inner expander with the view name as its header. The view editor owns the data-view selector, filter fields, output fields, and sources. Selected data-view ID/title and all view fields are persisted when the server is saved and restored when the settings panel is reopened.

## Compatibility and validation

Existing flat connections normalize to one view. Existing server IDs and source IDs are preserved. A server may save without a complete view; opening a source still validates the selected view. A view name defaults to its selected data-view title or `View` when absent.
