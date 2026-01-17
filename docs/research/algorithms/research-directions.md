## Framing: what you’re really optimizing

Local search in a large, heterogeneous codebase is a **budgeted evidence acquisition** problem over two coupled spaces:

- **Content space**: text/code/config/docs (chunks, symbols, AST nodes) with retrieval and ranking.
- **Relationship space**: edges between objects (imports, calls, references, ownership, build deps, runtime deps) with graph traversal/ranking.

A useful abstract model to keep in mind for research is:

- You have candidate evidence items (i \in \mathcal{C}) with **cost** (c_i) (tokens) and uncertain **utility** (u_i) (how much it helps answer/understand).
- You want to pick a subset (S) under a budget (B): maximize (U(S)) s.t. (\sum_{i\in S} c_i \le B).
- In practice, you also want *adaptive* selection: the next choice depends on what you already read (sequential decision-making).

Almost everything below maps to improving one of: candidate generation, ranking, graph-guided expansion, or utility estimation under budget.

------

## 1) Hybrid retrieval in one system: sparse + dense + learned sparse

Why it matters: codebases mix **exact tokens** (identifiers, error strings, flags) with **semantic intent** (conceptual queries, architectural questions). No single retriever dominates across both.

Research avenues:

- **Classical sparse IR** (BM25, fielded variants like BM25F) for identifiers and “needle” strings.
  - DuckDB’s Full-Text Search extension exposes BM25-style scoring primitives (e.g., `match_bm25`). ([DuckDB](https://duckdb.org/docs/stable/core_extensions/full_text_search.html?utm_source=chatgpt.com))
- **Dense retrieval** (bi-encoders / dual encoders) using embeddings + ANN indices.
  - DuckDB’s `vss` extension adds vector similarity search and indexing (HNSW). ([DuckDB](https://duckdb.org/docs/stable/core_extensions/vss.html?utm_source=chatgpt.com))
- **Learned sparse retrieval** (SPLADE and variants): produces sparse vectors that act like “neural BM25”, retaining lexical match benefits while learning expansion. ([arXiv](https://arxiv.org/abs/2107.05720?utm_source=chatgpt.com))
- **Late interaction retrieval** (ColBERT/ColBERTv2): a middle ground—more expressive than single-vector dense retrieval, cheaper than full cross-encoder ranking. ([arXiv](https://arxiv.org/abs/2004.12832?utm_source=chatgpt.com))
- **Rank fusion** for hybrid ensembles.
  - Reciprocal Rank Fusion (RRF) is a simple, high-leverage method for combining rankings from multiple retrievers. ([G. V. Cormack](https://cormack.uwaterloo.ca/cormacksigir09-rrf.pdf?utm_source=chatgpt.com))

Concrete “things to look up”:

- “BM25F fielded retrieval”, “SPLADE efficiency”, “ColBERT compression”, “hybrid retrieval RRF vs score fusion”.
- How to incorporate code-specific fields: `symbol_name`, `path`, `lang`, `docstring`, `callers/callees`, etc.

------

## 2) Efficient similarity search with constraints: ANN + filtered ANN

Why it matters: local search usually means you’re not searching *everything*—you’re searching “near” a region (subtree, module, package, owners, runtime component). That introduces **filters** (path prefixes, language, build target, package graph neighborhood) on top of vector search.

Research avenues:

- **HNSW** mechanics and tuning (construction ef, search ef, recall/latency tradeoffs). ([arXiv](https://arxiv.org/abs/1603.09320?utm_source=chatgpt.com))
- **Graph-based ANN surveys/benchmarks** (to understand when HNSW is enough vs alternatives). ([VLDB](https://www.vldb.org/pvldb/vol14/p1964-wang.pdf?utm_source=chatgpt.com))
- **Filtered ANN / hybrid vector-scalar retrieval** (vector search under metadata predicates), which becomes important when you have repository-local constraints. ([arXiv](https://arxiv.org/html/2505.06501v1?utm_source=chatgpt.com))

DuckDB relevance:

- DuckDB `vss` gives you HNSW indexing in-process; research is mostly about how you structure “filtered retrieval” and scoring pipelines on top. ([DuckDB](https://duckdb.org/docs/stable/core_extensions/vss.html?utm_source=chatgpt.com))

------

## 3) Two-stage ranking: rerankers + learning-to-rank

Why it matters: in code search, candidate generation must be cheap; precision comes from reranking a small candidate set.

Research avenues:

- **Cross-encoder reranking** (query + candidate jointly scored) for the last mile.
  - A practical reference implementation pattern is described in OpenAI’s reranking cookbook. ([OpenAI Cookbook](https://cookbook.openai.com/examples/search_reranking_with_cross-encoders?utm_source=chatgpt.com))
- **Learning-to-rank (LTR)** for combining heterogeneous features:
  - LambdaRank/LambdaMART are classic, strong baselines for feature-rich ranking (text scores + graph features + metadata + priors). ([Microsoft](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/MSR-TR-2010-82.pdf?utm_source=chatgpt.com))
- **Debiasing and feedback loops** if you plan to learn from developer interaction (clicks, “open file”, “jump to definition”, “copied snippet”).
  - Contextual bandit / online LTR literature is directly relevant (see section 11). ([Oxford Computer Science](https://www.cs.ox.ac.uk/people/shimon.whiteson/pubs/hofmannirj13.pdf?utm_source=chatgpt.com))

Key feature ideas to research:

- Graph proximity scores (PPR distance to seed), dependency distance, ownership proximity, test coverage linkage, recency, churn, build target inclusion.

------

## 4) Code-specific representation learning for retrieval

Why it matters: generic text embeddings miss semantics carried by structure: AST, dataflow, control flow, and naming conventions.

Research avenues (as starting points):

- **Benchmarks**:
  - CodeSearchNet gives a large-scale semantic code search benchmark and a way to compare approaches. ([arXiv](https://arxiv.org/abs/1909.09436?utm_source=chatgpt.com))
- **NL–code pretraining**:
  - CodeBERT (bimodal NL + code). ([arXiv](https://arxiv.org/abs/2002.08155?utm_source=chatgpt.com))
- **Structure-aware pretraining**:
  - GraphCodeBERT integrates dataflow/structure signals and reports gains on code search and related tasks. ([arXiv](https://arxiv.org/abs/2009.08366?utm_source=chatgpt.com))
- **Unified code representation**:
  - UniXcoder targets both understanding and generation tasks, useful if you later add summarization or “explain this symbol” capabilities. ([arXiv](https://arxiv.org/abs/2203.03850?utm_source=chatgpt.com))
- **AST-path based embeddings**:
  - code2vec and code2seq are older but still useful conceptually for “structure-first” representations. ([arXiv](https://arxiv.org/abs/1803.09473?utm_source=chatgpt.com))
- **GNNs over program graphs**:
  - Program analysis via graph neural networks is a direct bridge between your relationship graph and retrieval. ([Graph Neural Networks](https://graph-neural-networks.github.io/static/file/chapter22.pdf?utm_source=chatgpt.com))

What to explore next:

- “Embedding functions vs files vs symbols”: retrieval units matter as much as the model.
- Contrastive objectives that align “issue/stacktrace/test failure → code region”.

------

## 5) Structural (syntax-aware) search as a complementary retriever

Why it matters: a lot of developer queries are actually “find code shaped like X”, where regex is too weak and semantic embeddings are too fuzzy.

Research avenues:

- **Incremental parsing and syntax trees**:
  - Tree-sitter provides concrete syntax trees and incremental updates—useful for maintaining an always-fresh structural index. ([Tree-sitter](https://tree-sitter.github.io/?utm_source=chatgpt.com))
- **Structural pattern search**:
  - Sourcegraph’s structural search is a good conceptual reference for syntax-aware patterns (balanced delimiters, placeholders, etc.). ([Sourcegraph](https://sourcegraph.com/blog/how-to-search-with-sourcegraph-using-structural-patterns?utm_source=chatgpt.com))
- **Hybrid ranking**:
  - Treat structural matches as high-precision candidates, then rerank semantically (or vice versa).

Search terms:

- “tree query language”, “AST pattern matching”, “code property graph query DSL”, “structural search DSL”.

------

## 6) Relationship modeling: from “edges in tables” to code property graphs

Why it matters: understanding complex systems often reduces to navigating the right intermediate representations: AST, CFG, PDG, call graph, import graph, dataflow edges, config wiring.

Research avenues:

- **Program Dependence Graph (PDG)** as a foundation for control + data dependence. ([CSA - IISc Bangalore](https://www.csa.iisc.ac.in/~raghavan/CleanedPav2011/ferrante-pdg-1987.pdf?utm_source=chatgpt.com))
- **Code Property Graph (CPG)**: merges multiple representations into a single graph model for querying patterns at scale.
  - Joern’s docs/specs provide a concrete CPG model and query approach. ([Joern Documentation](https://docs.joern.io/code-property-graph/?utm_source=chatgpt.com))
  - The CPG idea is also described in vulnerability mining work. ([Comsecuris](https://comsecuris.com/papers/06956589.pdf?utm_source=chatgpt.com))

What to explore next:

- Which edges are “semantic” vs “incidental” for your use cases (e.g., import edges can be noisy; call edges might be dynamic; config edges may be implicit).
- Typed edges and edge priors for traversal/ranking.

------

## 7) Graph traversal + graph ranking for “local” exploration

Why it matters: local search is often graph-neighborhood search: “start here; find the next most relevant related nodes”.

Research avenues:

- **Personalized PageRank / Random Walk with Restart (RWR)** as a principled way to rank “nearby” nodes relative to a seed set. ([arXiv](https://arxiv.org/pdf/1408.0719?utm_source=chatgpt.com))
- **Graph embeddings** for proximity and clustering (DeepWalk/node2vec), especially if you want fast similarity search over graph neighborhoods. ([arXiv](https://arxiv.org/abs/1403.6652?utm_source=chatgpt.com))
- **Steiner tree / group Steiner tree** approaches for “connect these query entities” (useful when a query names multiple symbols/components and you want the relationship explanation graph).
  - Relationship query approximation (STAR). ([MPG.PuRe](https://pure.mpg.de/pubman/item/item_1819064_5/component/file_1840681/MPI-I-2008-5-001.pdf?utm_source=chatgpt.com))
  - Keyword search over graphs strongly overlaps with this framing. ([VLDB](https://www.vldb.org/pvldb/vol4/p681-kargar.pdf?utm_source=chatgpt.com))

DuckDB-specific avenues:

- DuckDB supports graph traversal via `WITH RECURSIVE`, including guidance on cycle detection for arbitrary graphs. ([DuckDB](https://duckdb.org/docs/stable/sql/query_syntax/with.html?utm_source=chatgpt.com))
- DuckDB’s `USING KEY` optimization for recursive CTEs is explicitly motivated by graph algorithms and can materially change feasibility for iterative traversals. ([DuckDB](https://duckdb.org/2025/05/23/using-key.html?utm_source=chatgpt.com))
- The DuckPGQ community extension implements SQL/PGQ graph pattern matching (SQL:2023) and path-finding syntax. ([DuckDB](https://duckdb.org/community_extensions/extensions/duckpgq.html?utm_source=chatgpt.com))

------

## 8) Keyword search over “relationalized graphs”: BANKS/DISCOVER lineage

Why it matters: you explicitly have “a DuckDB database mapping” the codebase. There is mature research on answering keyword queries over relational schemas by implicitly traversing join graphs and ranking resulting subgraphs/trees—very close to “search across heterogeneous codebase objects with relationships”.

Research avenues:

- **BANKS**: keyword search + browsing over relational data. ([CSE IIT Bombay](https://www.cse.iitb.ac.in/~sudarsha/Pubs-dir/BanksDemoVLDB2002.pdf?utm_source=chatgpt.com))
- **DISCOVER**: keyword search in relational databases with ranking of join results. ([Database Lab](https://dbucsd.github.io/paperpdfs/2002_8.pdf?utm_source=chatgpt.com))
- Later work and surveys on scalable keyword search over relational/graph data. ([VLDB](https://vldb.org/pvldb/vol3/R12.pdf?utm_source=chatgpt.com))

How it maps:

- Tables: `files`, `symbols`, `occurrences`, `edges`, `tests`, `build_targets`, `configs`.
- Queries: keywords + constraints.
- Output: a ranked *explanation subgraph* (not just a list of documents).

------

## 9) Multi-hop retrieval and graph-guided evidence chaining

Why it matters: “understanding relationships” usually requires multiple hops: doc → symbol → call sites → config → runtime entrypoint → tests.

Research avenues:

- **Dense retrieval as a component**:
  - DPR is a canonical dense retriever reference point. ([arXiv](https://arxiv.org/abs/2004.04906?utm_source=chatgpt.com))
- **Multi-hop QA framing**:
  - HotpotQA formalizes multi-hop evidence requirements and is useful as an evaluation analogue (even if your domain is code). ([arXiv](https://arxiv.org/abs/1809.09600?utm_source=chatgpt.com))
- **GraphRAG and graph-based RAG**:
  - GraphRAG proposes graph-based indexing/retrieval/summarization for private corpora. ([arXiv](https://arxiv.org/abs/2404.16130?utm_source=chatgpt.com))
  - A survey of GraphRAG techniques provides a taxonomy and evaluation framing. ([arXiv](https://arxiv.org/pdf/2408.08921?utm_source=chatgpt.com))
- **Dynamic / iterative retrieval policies** (deciding when to expand, stop, or change direction):
  - “Tree of Reviews” is one example of dynamic iterative retrieval decisions. ([arXiv](https://arxiv.org/abs/2404.14464?utm_source=chatgpt.com))
  - Lightweight graph-based multi-step retrievers without heavy LLM use also exist (useful if your bottleneck is token budget). ([ACL Anthology](https://aclanthology.org/2025.emnlp-industry.174/?utm_source=chatgpt.com))

Key research question:

- How to score **next-hop expansions**: lexical/semantic similarity vs graph proximity vs “bridgingness” (does this node connect clusters?).

------

## 10) Token-budgeted context selection: diversity, coverage, submodularity

Why it matters: your constraint (“information always exceeds token budget”) is fundamentally a **selection under budget** problem, not a retrieval problem.

Research avenues:

- **MMR (Maximal Marginal Relevance)**: balances relevance against redundancy; directly maps to “don’t spend tokens repeating the same fact from 5 files”. ([CMU School of Computer Science](https://www.cs.cmu.edu/~jgc/publication/The_Use_MMR_Diversity_Based_LTMIR_1998.pdf?utm_source=chatgpt.com))
- **Submodular maximization** for summarization/coverage:
  - Lin & Bilmes frame summarization as submodular maximization with strong practical implications (diminishing returns → greedy selection works well). ([ACL Anthology](https://aclanthology.org/P11-1052/?utm_source=chatgpt.com))
- **Budgeted maximum coverage / knapsack-submodular optimization**:
  - Approximation algorithms for “maximize utility under costs” (highly aligned with token budgets). ([ScienceDirect](https://www.sciencedirect.com/science/article/abs/pii/S0020019099000319?utm_source=chatgpt.com))
- **Practical outcome**:
  - Instead of “top-k chunks”, you move to “best set of chunks under budget” where utility includes diversity and graph coverage.

What to explore next:

- Utility functions that include: relevance score, novelty, graph coverage (cover important nodes/edges), and “bridging” nodes.

------

## 11) Expected value estimation: VOI + active acquisition + bandits

Why it matters: you want to spend tokens on the *most informative next piece*, not just the highest-ranked piece.

Research avenues:

- **Value of Information (VOI)**:
  - EVPI/VOI concepts formalize how much you should pay (in cost) to reduce decision uncertainty. ([PMC](https://pmc.ncbi.nlm.nih.gov/articles/PMC8160067/?utm_source=chatgpt.com))
- **Active feature-value acquisition (AFA)**:
  - AFA explicitly models choosing which costly features to acquire to improve prediction most cost-effectively—this maps cleanly to “choose which chunk/function/test/log to read next”. ([Stern School of Business](https://pages.stern.nyu.edu/~fprovost/Papers/AFA-MS-Final.pdf?utm_source=chatgpt.com))
- **Budgeted/cost-sensitive sequential acquisition**:
  - Framing information acquisition as a reinforcement learning problem under budget constraints is directly relevant when retrieval is interactive. ([OpenReview](https://openreview.net/pdf?id=S1eOHo09KX&utm_source=chatgpt.com))
- **Contextual bandits for IR / online learning-to-rank**:
  - Useful when you have interaction signals and need exploration/exploitation (e.g., the system must sometimes try less-obvious files to learn). ([Computer Science at UBC](https://www.cs.ubc.ca/~hutter/nips2011workshop/papers_and_posters/nips-2012-rl4ir.pdf?utm_source=chatgpt.com))

A practical research synthesis to aim for:

- Model each candidate item with ((\text{expected gain}, \text{uncertainty}, \text{cost})).
- Pick items by an acquisition rule (e.g., upper confidence bound, Thompson sampling, or VOI approximations) rather than raw relevance.

------

## 12) Query expansion and “retrieval robustness” techniques (high leverage, often overlooked)

Why it matters: developer queries are frequently underspecified (“where is auth handled?”), and local code terminology may not match.

Research avenues:

- **HyDE**: generate a hypothetical “ideal answer document”, embed it, then retrieve real docs near that embedding. This is a strong approach when you lack relevance labels. ([arXiv](https://arxiv.org/abs/2212.10496?utm_source=chatgpt.com))
- **GraphRAG-style global-to-local**:
  - Build higher-level summaries/communities first, then drill down. ([arXiv](https://arxiv.org/abs/2404.16130?utm_source=chatgpt.com))
- **Multi-query generation + fusion**:
  - Conceptually: generate multiple query variants and fuse results with RRF (ties back to section 1). ([G. V. Cormack](https://cormack.uwaterloo.ca/cormacksigir09-rrf.pdf?utm_source=chatgpt.com))

------

## 13) Evaluation methodology: make research measurable in your setting

Why it matters: without task-specific evaluation, it’s easy to optimize the wrong thing (especially with embeddings).

Research avenues:

- **Retrieval metrics**: Recall@k, MRR, nDCG; plus *latency vs recall* curves for ANN.
- **Budget curves**: “Answer quality vs tokens spent” (treat as a frontier).
- **Multi-hop metrics**: evidence chain correctness, path length, redundancy.
- **Code search benchmark anchors**:
  - Use CodeSearchNet-style tasks as a sanity check even if your domain differs. ([arXiv](https://arxiv.org/abs/1909.09436?utm_source=chatgpt.com))
- **GraphRAG evaluation framing** if you summarize and traverse. ([arXiv](https://arxiv.org/pdf/2408.08921?utm_source=chatgpt.com))

Practical ground-truth generation ideas (often missed):

- Use commit messages / PR descriptions ↔ changed symbols as weak labels.
- Use test failures ↔ stack traces ↔ touched files as query–answer pairs.
- Use IDE navigation logs (if available) as implicit relevance feedback.

------

## A compact “research keyword” list to drive your next deep dives

Retrieval & ranking:

- BM25F, SPLADE, ColBERT/late interaction, hybrid retrieval, RRF, cross-encoder reranking, LambdaMART.

Graph & relationships:

- code property graph (CPG), program dependence graph (PDG), call graph construction, random walk with restart, personalized PageRank, node2vec/DeepWalk, Steiner tree / group Steiner tree.

Budget & value:

- value of information (EVPI/EVSI), submodular maximization under knapsack, MMR diversification, active feature acquisition, contextual bandits for IR, safe exploration.

DuckDB-specific enabling tech (if you want to keep everything in DuckDB):

- DuckDB `fts` (BM25), DuckDB `vss` (HNSW), `WITH RECURSIVE` graph traversal, `USING KEY` optimization for recursive queries, DuckPGQ / SQL/PGQ. ([DuckDB](https://duckdb.org/docs/stable/core_extensions/full_text_search.html?utm_source=chatgpt.com))