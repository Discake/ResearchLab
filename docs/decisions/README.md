# Architecture decisions

Create an ADR when a consequential architectural trade-off is actually made: for example, changing an ownership boundary, persistence strategy or externally observable contract. Small local fixes do not require one. Do not reconstruct past rationale without evidence.

Use a short numbered Markdown file (`NNNN-short-title.md`) with a title, date and status, then **Context**, **Decision**, **Alternatives**, and **Consequences**. Statuses: **Proposed**, **Accepted**, **Superseded**. Proposed is not authorization to implement.

Preserve the original decision and rationale. When replacing a decision, mark it Superseded and link both records instead of rewriting history. Link accepted decisions from the relevant contract and [code map](../CODEMAP.md). No retrospective ADRs have been created here.
