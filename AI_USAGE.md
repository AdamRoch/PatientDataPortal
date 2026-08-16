# AI Usage

## Development assistance

The project used AI-assisted development tools for planning and implementation support:

- **OpenAI Codex** was used to help plan project work and assist with implementation.
- **Fable** was used for an architecture and delivery-plan review; the retained review is in [`fable-review.md`](fable-review.md).

These tools assisted the human author. Design decisions and code changes remain subject
to repository review, tests, and the documented ADRs.

## Runtime AI

v1 contains no runtime AI or LLM capability. In particular, the optional
natural-language booking stretch is out of scope, so the application does not send
patient data or booking requests to an AI model. There is no runtime model, engine,
prompt, or AI configuration to disclose.
