<WHY>
Keywords reshapes the rough handles you already have into the repository's real vocabulary — symbols, domain nouns, path/config constants. It ranks whole contexts inside `uriGlob` by hybrid search, names each context, and reranks the named candidates so the un-guessable handle surfaces. It is a vocabulary scout, not an answer engine.
</WHY>

<WHEN_TO_USE>
Use it when your current words are too generic, borrowed from another codebase, or likely to miss the local names.

Good fits:
- Cold-starting a domain where you need the real terms before calling explore or read.
- "What do we call X here?" — describe the concept in your own words in `question`; the novel local name leads discovery, while any of your words that already name something here are confirmed with evidence.
- Building search queries from repository-grounded terms.
- Finding noun-dense model/entity clusters, config constants, paths, and local symbols.

Weak fits:
- Behavioral or framework-wired targets such as DI registrations, attributes, generic dispatch, and event handlers.
- Lexical collisions such as "schedule" meaning volunteer rostering in one area and payment schedules in another.

For weak fits, call explore directly with the best returned terms.

Two-stage flow when you don't know where to look: run one broad probe to find the subsystem, then a second probe scoped to that subsystem to surface deeper symbol names.
</WHEN_TO_USE>

<OUTPUT>
When a word you supplied literally names something here and reranks as relevant, it opens the response under `Confirmed` — your term, validated, with its evidence URI. A supplied word missing from Confirmed found nothing strong; the omission is the verdict. Weak literal matches are dropped, never rendered.

Discovery terms render with a confidence percentage. Treat 55%+ as a strong hit, 30–55% as a possible lead, below 30% as noise. The percentage blends the rerank score with the recall score — higher means more evidence the term answers your question, not just lexical overlap. Terms that merely repeat your question's own words are discounted: the tool optimizes for vocabulary you don't have.

File-scoped searches automatically include the repository's concept capsules (`concept://`) — its glossary. Mention `concept://` anywhere in the glob, even negated, to take control of that.

Terms group by evidence kind: `Names` (production symbols and concept phrases) first, then `Config & paths`, `Tests`, and `Docs`. Tighter scope populates `Names` more. The renderer caps visible terms at 20, spends at most two on any single file, and keeps a seat for every facet of the question — so the list reads as a map of distinct clusters, not one hot file repeated.

Row kinds in the structured response:
- `confirmed`: a supplied term found literally present and relevant, with `evidence_json`.
- `term`: an individual ranked term with `evidence_json` and a compact snippet when available. Terms come in symbol form (`DirtySweep`) and phrase form (`dirty sweep`) — whichever the reranker judged more relevant to your question survives, and the evidence URI carries the other.
- `diagnostic`: index or scope state — for example `empty_pool` when nothing surfaced.

The rendered output never exceeds the token budget.
</OUTPUT>
