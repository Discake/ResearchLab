# Metric protocol — M4.1

`MetricProtocol.Parse(ReadOnlySpan<byte>)` in Application parses one complete stdout
line. It has no Run, storage, runner or filesystem dependency. It returns exactly
one of `MetricParseResult.Metric`, `OrdinaryLine`, or `Error(Code)`. No metric is
created on error; even a valid DTO is not a persisted `MetricPoint`.

```text
@researchlab/v1 {"type":"metric","name":"residual","value":0.0024,"step":15,"unit":"1"}
```

The marker is case sensitive at byte zero, followed by one ASCII space and a JSON
object. Additional JSON whitespace is allowed. A missing separator or tab separator
is an invalid envelope. A marker with a non-whitespace suffix (including v10), an
indented marker, and embedded markers are ordinary logs. Unknown protocol versions
are ordinary logs in this increment. Input may omit the terminator or end in one LF
or CRLF; embedded raw CR/LF is rejected. No BOM is accepted before the marker.
The caller frames lines and retains original bytes for **all** results, including
ordinary logs and rejected events; the parser neither consumes a stream nor drops logs.
Non-event bytes are not decoded, so invalid UTF-8 in ordinary logs stays ordinary.

Events use strict UTF-8 without replacement decoding. Invalid UTF-8 returns
`InvalidUtf8`; malformed JSON (including invalid surrogate escapes), comments,
trailing commas, extra JSON values and excessive depth return `InvalidJson`.
All property names are case sensitive. Duplicate decoded keys, including escaped
equivalents, return `DuplicateField`. `type` is required and must be a string;
anything other than `metric`, explicitly including `artifact`, returns
`UnsupportedEventType`. Artifact payloads are not interpreted. For metrics, unknown
fields return `UnknownField` and missing name/value return `MissingField`.

| Field | Contract |
| --- | --- |
| `type` | Required string, exactly `metric`. |
| `name` | Required string, not blank, no Unicode control characters. Preserved verbatim. |
| `value` | Required JSON number convertible to finite double; overflow rejected. IEEE double rounding and underflow apply. |
| `step` | Optional signed Int64 integer token, including negative values. Fractions, exponent notation and overflow rejected. |
| `unit` | Optional nonblank string without Unicode control characters. Preserved verbatim. |

Explicit null is invalid for every field. Wrong JSON kinds return `InvalidFieldType`;
invalid text returns `InvalidName`/`InvalidUnit`, nonfinite conversion `InvalidValue`,
and invalid integer tokens `InvalidStep`. Unknown scientific units are accepted:
there is no whitelist, scientific grammar, normalization or conversion. This is a
text syntax check only; Domain still enforces exact unit compatibility within a series.
Unpaired surrogate escapes are rejected, never silently replaced.

## Resource limits

- Maximum **8192 bytes**, including marker/separator/JSON/trailing whitespace,
  excluding one final LF or CRLF. Checked before decoding or JSON allocation.
  This allows small numeric events with descriptive Unicode names while bounding
  per-call memory and CPU far below the existing 1 MiB combined stdout/stderr Run cap.
- Maximum JSON depth **4**; valid metric objects need only depth 1.
- Allocations are bounded by event size (payload copy, JSON document, decoded keys
  and strings). No accumulation across calls. Ordinary lines need no payload copy.
- One diagnostic enum code per line; no user content, paths, JSON exception text or
  unbounded snippets. The first detected error is returned, not a complete error list.

Malformed user data returns errors rather than throwing. Runtime resource exhaustion
is not a recoverable protocol error. Callers must frame streams with bounded buffering;
this parser cannot bound memory allocated by its caller before the call.

## Deferred policy

This increment does not ingest, timestamp, sequence, persist or mutate Runs. It does
not decide whether a diagnostic fails a Run. Event counts, total metrics, diagnostic
retention/rate limits and ingestion behavior at stdout truncation need an integration
policy. The 1 MiB Run cap is unchanged and does not replace bounded line framing.
Scientific unit grammar/registries and richer compatibility policies remain open;
future changes must preserve evidence and avoid silent conversion. Stdout is data,
never instructions for ResearchLab. Artifacts, SDKs and UI are outside M4.1.
