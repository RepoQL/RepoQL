# Information Theory for Retrieval

Mathematical foundations of information theory as applied to search and retrieval systems.

## Table of Contents

1. [Overview](#overview)
2. [Entropy and Information Content](#entropy-and-information-content)
   - [Shannon Entropy](#shannon-entropy)
   - [Cross-Entropy](#cross-entropy)
   - [Conditional Entropy](#conditional-entropy)
   - [Application: Measuring Document Informativeness](#application-measuring-document-informativeness)
3. [Mutual Information](#mutual-information)
   - [Definition and Properties](#definition-and-properties)
   - [Application: Term-Document Relevance](#application-term-document-relevance)
   - [Application: Feature Selection](#application-feature-selection)
4. [KL Divergence and JS Divergence](#kl-divergence-and-js-divergence)
   - [Kullback-Leibler Divergence](#kullback-leibler-divergence)
   - [Jensen-Shannon Divergence](#jensen-shannon-divergence)
   - [Application: Comparing Distributions](#application-comparing-distributions)
5. [Information Gain and Feature Selection](#information-gain-and-feature-selection)
   - [Information Gain](#information-gain)
   - [Information Gain Ratio](#information-gain-ratio)
   - [Application: Query Expansion and Term Weighting](#application-query-expansion-and-term-weighting)
6. [Rate-Distortion Theory](#rate-distortion-theory)
   - [Fundamentals](#fundamentals)
   - [Application: Compression and Summarization](#application-compression-and-summarization)
7. [Information Bottleneck Method](#information-bottleneck-method)
   - [The IB Principle](#the-ib-principle)
   - [Application: Learning Compressed Representations](#application-learning-compressed-representations)
8. [Connections to Retrieval](#connections-to-retrieval)
   - [Cross-Entropy Loss in Neural Retrieval](#cross-entropy-loss-in-neural-retrieval)
   - [Entropy-Based Diversity (DPP Connection)](#entropy-based-diversity-dpp-connection)
   - [Information-Theoretic Query Difficulty](#information-theoretic-query-difficulty)
9. [Practical Applications for Code Search](#practical-applications-for-code-search)
10. [References](#references)

---

## Overview

Information theory, founded by Claude Shannon in 1948, provides the mathematical framework for quantifying information, uncertainty, and the limits of communication. For retrieval systems, these concepts are foundational:

| Concept | Retrieval Application |
|---------|----------------------|
| Entropy | Measuring query/document uncertainty, diversity |
| Mutual Information | Quantifying term-relevance relationships |
| KL Divergence | Comparing query and document distributions |
| Cross-Entropy | Training neural retrieval models |
| Rate-Distortion | Understanding compression/summarization limits |
| Information Bottleneck | Learning minimal sufficient representations |

**Why Information Theory Matters for Search:**

1. **Principled term weighting**: Information-theoretic measures (IDF, mutual information) quantify how much a term "tells us" about relevance
2. **Query difficulty estimation**: Entropy-based metrics predict when queries will fail
3. **Diversity optimization**: Entropy maximization ensures result variety
4. **Neural retrieval training**: Cross-entropy loss is the standard objective for dense retrievers
5. **Compression bounds**: Rate-distortion theory tells us the limits of summarization

---

## Entropy and Information Content

### Shannon Entropy

**Definition**: Shannon entropy measures the average uncertainty (or information content) of a random variable.

For a discrete random variable X with possible values {x_1, x_2, ..., x_n} and probability mass function P(X):

```
H(X) = -sum_{i=1}^{n} P(x_i) * log_2(P(x_i))
```

**Properties:**

| Property | Formula/Condition |
|----------|-------------------|
| Non-negativity | H(X) >= 0 |
| Maximum | H(X) <= log_2(n), equality iff P is uniform |
| Minimum | H(X) = 0 iff X is deterministic |
| Concavity | H is strictly concave in P |

**Units:**
- Base 2: bits (shannons)
- Base e: nats
- Base 10: hartleys (dits)

**Intuition**: Entropy quantifies "surprise." A fair coin (H = 1 bit) is maximally surprising; a biased coin (p = 0.99) has low entropy because outcomes are predictable.

**Example - Document Term Distribution:**

Consider a document with term frequencies:

| Term | Count | P(term) | -P*log_2(P) |
|------|-------|---------|-------------|
| "function" | 10 | 0.25 | 0.500 |
| "class" | 8 | 0.20 | 0.464 |
| "return" | 6 | 0.15 | 0.411 |
| "if" | 6 | 0.15 | 0.411 |
| "var" | 4 | 0.10 | 0.332 |
| "for" | 4 | 0.10 | 0.332 |
| "while" | 2 | 0.05 | 0.216 |
| **Total** | 40 | 1.00 | **H = 2.67 bits** |

A document with uniform term distribution would have H = log_2(7) = 2.81 bits. The lower entropy indicates some terms dominate.

### Cross-Entropy

**Definition**: Cross-entropy measures the average number of bits needed to encode samples from distribution P using a code optimized for distribution Q.

```
H(P, Q) = -sum_{x} P(x) * log_2(Q(x))
```

**Relationship to Entropy:**

```
H(P, Q) = H(P) + D_KL(P || Q)
```

where D_KL is the Kullback-Leibler divergence. Cross-entropy is always at least as large as entropy, with equality when P = Q.

**In Neural Retrieval**: Cross-entropy loss is used to train models where P is the true relevance distribution (often one-hot) and Q is the model's predicted distribution over documents.

### Conditional Entropy

**Definition**: Conditional entropy measures the remaining uncertainty in X given knowledge of Y.

```
H(X | Y) = sum_{y} P(y) * H(X | Y=y)
         = -sum_{x,y} P(x,y) * log_2(P(x|y))
```

**Chain Rule:**

```
H(X, Y) = H(X) + H(Y | X) = H(Y) + H(X | Y)
```

**Intuition**: H(X|Y) tells us how much uncertainty remains about X after observing Y. If Y completely determines X, then H(X|Y) = 0.

**Example - Query Given Document:**

If we know a document is about "authentication," how much uncertainty remains about the query terms? Low conditional entropy means the document strongly predicts query terms.

### Application: Measuring Document Informativeness

**Document Entropy as Quality Signal:**

High-entropy documents use diverse vocabulary and may be:
- More comprehensive
- Less focused
- Potentially more informative for exploratory queries

Low-entropy documents have repetitive vocabulary and may be:
- Highly focused on a specific topic
- Better for precise queries
- Potentially less informative overall

**Term Entropy for Collection Analysis:**

```
H_collection = -sum_{t in V} P(t) * log_2(P(t))
```

where V is the vocabulary. This measures vocabulary diversity across the corpus.

**Normalized Entropy for Comparison:**

```
H_normalized = H(X) / log_2(n)
```

This scales entropy to [0, 1] for comparing documents of different vocabulary sizes.

---

## Mutual Information

### Definition and Properties

**Definition**: Mutual information quantifies the amount of information that one random variable contains about another.

```
I(X; Y) = sum_{x,y} P(x,y) * log_2(P(x,y) / (P(x) * P(y)))
```

**Equivalent Formulations:**

```
I(X; Y) = H(X) - H(X | Y)           // Reduction in uncertainty about X given Y
        = H(Y) - H(Y | X)           // Reduction in uncertainty about Y given X
        = H(X) + H(Y) - H(X, Y)     // Shared information
        = D_KL(P(X,Y) || P(X)P(Y))  // Divergence from independence
```

**Properties:**

| Property | Description |
|----------|-------------|
| Non-negativity | I(X; Y) >= 0 |
| Symmetry | I(X; Y) = I(Y; X) |
| Zero iff independent | I(X; Y) = 0 iff X and Y are independent |
| Upper bound | I(X; Y) <= min(H(X), H(Y)) |

**Pointwise Mutual Information (PMI):**

For specific values x and y:

```
PMI(x, y) = log_2(P(x,y) / (P(x) * P(y)))
```

PMI is positive when x and y co-occur more than expected by chance, negative when they co-occur less, and zero when independent.

### Application: Term-Document Relevance

**Term-Class Mutual Information:**

For text classification, MI between term t and class c measures how informative the term is for classification:

```
I(T; C) = sum_{t in {0,1}} sum_{c in C} P(t,c) * log_2(P(t,c) / (P(t) * P(c)))
```

where T=1 indicates term presence and T=0 indicates absence.

**Maximum MI**: A term achieves maximum MI with a class when it appears if and only if the document belongs to that class (perfect indicator).

**Example - Term Relevance:**

| Term | P(term, relevant) | P(term) | P(relevant) | PMI |
|------|-------------------|---------|-------------|-----|
| "authentication" | 0.08 | 0.10 | 0.20 | log_2(0.08 / 0.02) = 2.0 |
| "the" | 0.19 | 0.95 | 0.20 | log_2(0.19 / 0.19) = 0.0 |
| "deprecated" | 0.01 | 0.05 | 0.20 | log_2(0.01 / 0.01) = 0.0 |

"Authentication" has high PMI with relevance because it co-occurs with relevant documents far more than expected.

### Application: Feature Selection

**MI-Based Feature Selection:**

Select terms that maximize mutual information with the target class:

```
Selected_terms = argmax_{T subset V, |T|=k} sum_{t in T} I(t; C)
```

**Advantages:**
- Captures non-linear relationships
- Works with categorical variables
- Theoretically grounded

**Limitations:**
- Assumes feature independence
- May over-weight rare terms
- Computationally intensive for large vocabularies

**Weighted Average PMI (WAPMI):**

Addresses MI's bias toward rare terms:

```
WAPMI(t, c) = P(t) * PMI(t, c)
```

This weights PMI by term frequency, penalizing rare terms.

**Conditional Mutual Information Maximin (CMIM):**

Addresses feature redundancy by selecting features that are individually discriminating but weakly dependent on already-selected features:

```
t* = argmax_{t not in S} min_{s in S} I(t; C | s)
```

---

## KL Divergence and JS Divergence

### Kullback-Leibler Divergence

**Definition**: KL divergence (relative entropy) measures how one probability distribution diverges from another.

```
D_KL(P || Q) = sum_{x} P(x) * log_2(P(x) / Q(x))
```

**Properties:**

| Property | Value/Condition |
|----------|-----------------|
| Non-negativity | D_KL(P \|\| Q) >= 0 |
| Zero | D_KL(P \|\| Q) = 0 iff P = Q |
| Asymmetry | D_KL(P \|\| Q) != D_KL(Q \|\| P) in general |
| Unbounded | Can be infinite if Q(x) = 0 where P(x) > 0 |

**Intuition**: KL divergence measures the "cost" of using distribution Q to approximate P. It quantifies the extra bits needed to encode samples from P using a code optimized for Q.

**Asymmetry Implications:**

- D_KL(P || Q): "How much information is lost when Q is used to approximate P"
- D_KL(Q || P): "How much information is lost when P is used to approximate Q"

These are different questions with different answers.

### Jensen-Shannon Divergence

**Definition**: JS divergence is a symmetric, bounded alternative to KL divergence.

```
JSD(P || Q) = (1/2) * D_KL(P || M) + (1/2) * D_KL(Q || M)
```

where M = (P + Q) / 2 is the mixture distribution.

**Properties:**

| Property | Value |
|----------|-------|
| Symmetry | JSD(P \|\| Q) = JSD(Q \|\| P) |
| Bounded | 0 <= JSD(P \|\| Q) <= 1 (with log_2) |
| Zero | JSD(P \|\| Q) = 0 iff P = Q |
| Metric | sqrt(JSD) satisfies triangle inequality |

**Advantages over KL:**
- Always finite (no division by zero issues)
- Symmetric
- Bounded, making it easier to interpret
- Square root is a true metric

### Application: Comparing Distributions

**Query-Document Distribution Comparison:**

Model queries and documents as probability distributions over terms, then measure divergence:

```
Query model:    P_q(t) = tf(t, q) / |q|
Document model: P_d(t) = tf(t, d) / |d|

Relevance ~ -D_KL(P_q || P_d)  // Lower divergence = more relevant
```

**Language Model Retrieval:**

The query likelihood model ranks documents by:

```
score(d, q) = sum_{t in q} log P(t | d)
```

This is equivalent to minimizing cross-entropy H(P_q, P_d), which relates to KL divergence.

**Document Clustering with JSD:**

JSD's symmetry makes it ideal for clustering:

```
distance(d_1, d_2) = JSD(P_{d_1} || P_{d_2})
```

Documents with similar term distributions cluster together.

**Topic Model Comparison:**

Compare topic distributions from LDA:

```
topic_similarity(d_1, d_2) = 1 - JSD(theta_{d_1} || theta_{d_2})
```

---

## Information Gain and Feature Selection

### Information Gain

**Definition**: Information gain measures the reduction in entropy achieved by partitioning data on an attribute.

```
IG(S, A) = H(S) - sum_{v in Values(A)} (|S_v| / |S|) * H(S_v)
```

where S is the dataset, A is an attribute, and S_v is the subset where A = v.

**Equivalence**: Information gain equals mutual information between attribute A and class C:

```
IG(A) = I(A; C) = H(C) - H(C | A)
```

**For Binary Classification:**

```
IG(t) = H(C) - [P(t) * H(C|t) + P(not t) * H(C|not t)]
```

where t indicates term presence.

### Information Gain Ratio

**Definition**: Information gain ratio normalizes IG by the intrinsic information of the split:

```
IGR(S, A) = IG(S, A) / IV(A)
```

where intrinsic value is:

```
IV(A) = -sum_{v in Values(A)} (|S_v| / |S|) * log_2(|S_v| / |S|)
```

**Purpose**: IGR penalizes attributes with many values (which naturally achieve high IG but may not generalize).

### Application: Query Expansion and Term Weighting

**IGR for Query Expansion:**

When expanding queries, weight candidate terms by their information gain ratio with respect to initial results:

```
weight(t) = IGR(top_docs, t)
```

Terms that best distinguish top-ranked documents from the collection are good expansion candidates.

**Term Weighting with IG:**

Traditional TF-IDF can be enhanced with IG:

```
weight(t, d) = tf(t, d) * IG(t)
```

This weights terms by both frequency and discriminative power.

**Pseudo-Relevance Feedback:**

1. Issue initial query
2. Assume top-k documents are relevant
3. Compute IG for all terms in top-k
4. Add high-IG terms to query
5. Re-rank with expanded query

**Example:**

| Candidate Term | IG with Relevance | Selected? |
|----------------|-------------------|-----------|
| "oauth" | 0.42 | Yes |
| "jwt" | 0.38 | Yes |
| "token" | 0.31 | Yes |
| "security" | 0.15 | Maybe |
| "code" | 0.03 | No |

---

## Rate-Distortion Theory

### Fundamentals

**Definition**: Rate-distortion theory establishes the fundamental limits of lossy compression: the minimum bit rate required to represent a source at a given distortion level.

**Rate-Distortion Function:**

```
R(D) = min_{P(hat{X}|X): E[d(X, hat{X})] <= D} I(X; hat{X})
```

where:
- X is the source
- hat{X} is the reconstruction
- d(X, hat{X}) is the distortion measure
- D is the maximum allowed average distortion

**Key Insight**: There is a fundamental trade-off between:
- **Rate**: Bits required to represent the source
- **Distortion**: Information lost in compression

Lower rates require accepting higher distortion; zero distortion requires rate >= H(X).

**Distortion Measures:**

| Measure | Formula | Use Case |
|---------|---------|----------|
| Hamming | d(x, hat{x}) = 1 if x != hat{x}, 0 otherwise | Discrete sources |
| Squared error | d(x, hat{x}) = (x - hat{x})^2 | Continuous, Gaussian |
| Semantic | d(x, hat{x}) = 1 - similarity(x, hat{x}) | Text/documents |

### Application: Compression and Summarization

**Text Summarization as Rate-Distortion:**

Recent research frames summarization as a rate-distortion problem:

- **Source**: Original document X
- **Rate**: Summary length (tokens/bytes)
- **Distortion**: Semantic loss, measured by embedding distance or task performance

The summarizer rate-distortion function R(D) provides a fundamental lower bound on summary length for a given quality level.

**Implications for RepoQL:**

1. **Budget Allocation**: Given a token budget, rate-distortion theory tells us the minimum distortion achievable
2. **Summarization Quality**: Evaluate summarizers against the theoretical bound
3. **Progressive Disclosure**: Design summaries that achieve rate-distortion optimality at each detail level

**Prompt Compression:**

For LLM context compression:

```
minimize I(X; Z)           // Rate: compressed context
subject to I(Z; Y) >= I_0  // Distortion: preserve task-relevant information
```

where Z is the compressed prompt and Y is the desired output.

---

## Information Bottleneck Method

### The IB Principle

**Definition**: The Information Bottleneck method finds a compressed representation T of input X that preserves information about a relevance variable Y.

**Objective:**

```
min_{P(T|X)} I(X; T) - beta * I(T; Y)
```

where:
- I(X; T): Compression term (minimize)
- I(T; Y): Relevance term (maximize)
- beta: Trade-off parameter

**Intuition**: Find the simplest (most compressed) representation that still captures what's relevant about Y.

**Lagrangian Form:**

```
L = I(T; Y) - beta * I(X; T)
```

For large beta, we prioritize compression; for small beta, we prioritize relevance.

**Self-Consistent Equations:**

The optimal P(T|X) satisfies:

```
P(t|x) = P(t)/Z(x, beta) * exp(-beta * D_KL(P(Y|x) || P(Y|t)))
```

where Z is a normalization constant.

### Application: Learning Compressed Representations

**Deep Learning Interpretation:**

Each layer of a neural network can be viewed through the IB lens:

- Hidden layer H creates representation of input X
- Training minimizes cross-entropy, maximizing I(H; Y)
- Regularization and bottleneck architectures control I(X; H)

**Two-Phase Learning Hypothesis:**

1. **Fitting phase**: I(H; Y) increases rapidly
2. **Compression phase**: I(X; H) decreases while I(H; Y) plateaus

This hypothesis, proposed by Tishby and Shwartz-Ziv, suggests DNNs learn efficient representations by discarding input information irrelevant to the task.

**Caveats:**
- Compression phase depends on activation function (observed with tanh, not ReLU)
- Relationship between compression and generalization is debated
- MI estimation in high dimensions is challenging

**Application to Embeddings:**

Dense retrievers learn embeddings that can be analyzed via IB:

```
Embedding e of document d:
- Minimize I(d; e)  // Compact representation
- Maximize I(e; q)  // Preserve query-relevance information
```

The embedding dimension controls the rate; training optimizes the relevance-compression trade-off.

---

## Connections to Retrieval

### Cross-Entropy Loss in Neural Retrieval

**Dual-Encoder Training:**

Dense retrievers use cross-entropy loss over the softmax of similarity scores:

```
L = -log(exp(sim(q, d+)) / sum_{d in D} exp(sim(q, d)))
```

where d+ is the relevant document and D includes negatives.

**Connection to Information Theory:**

- Cross-entropy loss minimizes H(P_true, P_model)
- Equivalent to maximizing likelihood under the model
- Minimizes KL divergence from true relevance distribution

**Challenges:**

| Challenge | Solution |
|-----------|----------|
| Large partition function | Negative sampling, noise contrastive estimation |
| Memory for all document embeddings | In-batch negatives, cached embeddings |
| Hard negative selection | BM25 negatives, iterative hard negative mining |

**InfoNCE Loss:**

The noise contrastive estimation objective:

```
L_NCE = -log(exp(sim(q, d+) / tau) / sum_{i=1}^{K} exp(sim(q, d_i) / tau))
```

This is a lower bound on mutual information I(Q; D).

### Entropy-Based Diversity (DPP Connection)

**Determinantal Point Processes (DPPs):**

DPPs are probabilistic models that naturally encourage diversity through repulsion. The probability of selecting a set S is:

```
P(S) proportional to det(L_S)
```

where L is a positive semi-definite kernel matrix capturing item similarities.

**Connection to Entropy:**

- DPPs maximize a form of entropy over item selections
- Items with orthogonal features span larger volumes (higher probability)
- Similar items have near-zero probability of co-selection

**Application to Search Diversity:**

Given a set of relevant documents, use DPP to select a diverse subset:

```
maximize det(L_S) * prod_{d in S} quality(d)
```

This balances individual quality with set diversity.

**Practical Implementation:**

1. Encode documents as vectors v_d
2. Build kernel matrix: L_{ij} = relevance_i * relevance_j * v_i^T * v_j
3. Sample or find MAP estimate from DPP
4. Return selected diverse set

### Information-Theoretic Query Difficulty

**Clarity Score:**

The clarity score predicts query performance using KL divergence:

```
Clarity(q) = D_KL(P_q || P_C) = sum_t P(t|q) * log(P(t|q) / P(t|C))
```

where:
- P(t|q): Query language model (from top-ranked documents)
- P(t|C): Collection language model

**Interpretation:**
- High clarity: Query model diverges from collection (focused, specific)
- Low clarity: Query model similar to collection (ambiguous, broad)

**Empirical Finding**: Clarity score correlates positively with average precision across TREC benchmarks.

**Other Query Difficulty Predictors:**

| Predictor | Basis | Computation |
|-----------|-------|-------------|
| Clarity Score | KL divergence | Post-retrieval |
| Query Scope | IDF sum | Pre-retrieval |
| Robustness Score | Rank perturbation | Post-retrieval |
| Covering Topic Score | Topic coverage | Post-retrieval |

**Application:**
- Route difficult queries to more powerful models
- Trigger clarification requests for ambiguous queries
- Adjust retrieval strategies based on predicted difficulty

---

## Practical Applications for Code Search

Information theory provides principled foundations for code search systems:

### 1. Term Weighting for Code

Code has different term distributions than natural language:

| Consideration | Approach |
|---------------|----------|
| Identifier naming conventions | Entropy-based subtoken weighting |
| Keyword frequency | Down-weight via IG (low information gain) |
| API names | Up-weight via MI with functionality |
| Comments vs. code | Separate models with cross-entropy combination |

**Example - Identifier Entropy:**

Split `getUserAuthToken` into subtokens and weight by entropy contribution:

| Subtoken | Corpus P(t) | -log_2(P(t)) | Weight |
|----------|-------------|--------------|--------|
| "get" | 0.15 | 2.74 | Low |
| "User" | 0.08 | 3.64 | Medium |
| "Auth" | 0.02 | 5.64 | High |
| "Token" | 0.03 | 5.06 | High |

### 2. Query-Code Alignment

Use KL divergence to measure query-code distribution mismatch:

```
score(code, query) = -D_KL(P_query || P_code)
```

where distributions are over semantic concepts, not just terms.

### 3. Result Diversification

Apply DPP-based diversity to ensure search results cover:
- Different implementations
- Multiple programming patterns
- Various file types (tests, implementation, docs)

### 4. Summarization Budgets

Use rate-distortion principles to allocate explanation tokens:

```
tokens(file) proportional to H(file) * relevance(file, query)
```

High-entropy (complex) files with high relevance get more tokens.

### 5. Embedding Quality Evaluation

Evaluate code embeddings through an information-theoretic lens:

```
Quality = I(embedding; task_label) / I(code; embedding)
```

This measures efficiency: how much task-relevant information is preserved per bit of embedding.

### 6. Query Difficulty for Code Search

Adapt clarity score for code:

```
Clarity_code(q) = D_KL(P_q || P_codebase)
```

Low clarity suggests:
- Ambiguous natural language query
- Query matches common patterns
- Need for query refinement or clarification

---

## References

### Foundational Papers

1. **Shannon, C. E. (1948)**. "A Mathematical Theory of Communication." *Bell System Technical Journal*, 27(3), 379-423.
   - [Original paper (PDF)](https://people.math.harvard.edu/~ctm/home/text/others/shannon/entropy/entropy.pdf)
   - The foundational work establishing information theory

2. **Kullback, S., & Leibler, R. A. (1951)**. "On Information and Sufficiency." *Annals of Mathematical Statistics*, 22(1), 79-86.
   - Introduced KL divergence (relative entropy)

3. **Lin, J. (1991)**. "Divergence Measures Based on the Shannon Entropy." *IEEE Transactions on Information Theory*, 37(1), 145-151.
   - Introduced Jensen-Shannon divergence

4. **Tishby, N., Pereira, F. C., & Bialek, W. (1999)**. "The Information Bottleneck Method." *37th Allerton Conference on Communication, Control, and Computing*.
   - Foundational paper on the information bottleneck principle

### Information Retrieval Applications

5. **Cronen-Townsend, S., Zhou, Y., & Croft, W. B. (2002)**. "Predicting Query Performance." *SIGIR '02*.
   - [ACM Digital Library](https://dl.acm.org/doi/10.1145/564376.564429)
   - Introduced the clarity score for query difficulty prediction

6. **Zhai, C., & Lafferty, J. (2001)**. "A Study of Smoothing Methods for Language Models Applied to Ad Hoc Information Retrieval." *SIGIR '01*.
   - Language model approach to IR using KL divergence

7. **Yang, Y., & Pedersen, J. O. (1997)**. "A Comparative Study on Feature Selection in Text Categorization." *ICML '97*.
   - Comparison of MI, IG, and other feature selection methods

### Neural Retrieval

8. **Karpukhin, V., et al. (2020)**. "Dense Passage Retrieval for Open-Domain Question Answering." *EMNLP 2020*.
   - Dense retrieval with cross-entropy training

9. **Menon, A. K., et al. (2022)**. "In Defense of Dual-Encoders for Neural Ranking." *ICML 2022*.
   - [Paper](https://proceedings.mlr.press/v162/menon22a/menon22a.pdf)
   - Analysis of softmax cross-entropy for retrieval

10. **Tonellotto, N. (2022)**. "Lecture Notes on Neural Information Retrieval."
    - [arXiv:2207.13443](https://arxiv.org/pdf/2207.13443)
    - Comprehensive tutorial on neural IR including loss functions

### Diversity and DPPs

11. **Kulesza, A., & Taskar, B. (2012)**. "Determinantal Point Processes for Machine Learning." *Foundations and Trends in Machine Learning*, 5(2-3).
    - [Full text (PDF)](http://www.alexkulesza.com/pubs/dpps_fnt12.pdf)
    - Comprehensive treatment of DPPs for ML applications

### Rate-Distortion and Compression

12. **Arda, E., & Yener, A. (2025)**. "A Rate-Distortion Framework for Summarization."
    - [arXiv:2501.13100](https://arxiv.org/abs/2501.13100)
    - Recent work applying rate-distortion theory to text summarization

### Information Bottleneck in Deep Learning

13. **Shwartz-Ziv, R., & Tishby, N. (2017)**. "Opening the Black Box of Deep Neural Networks via Information."
    - [arXiv:1703.00810](https://arxiv.org/abs/1703.00810)
    - IB analysis of deep learning (note: some findings contested)

14. **Saxe, A. M., et al. (2019)**. "On the Information Bottleneck Theory of Deep Learning." *JMLR*, 20.
    - [Paper](https://openreview.net/pdf?id=ry_WPG-A-)
    - Critical analysis of IB claims in deep learning

### Additional Resources

- [Wikipedia: Entropy (information theory)](https://en.wikipedia.org/wiki/Entropy_(information_theory))
- [Wikipedia: KL Divergence](https://en.wikipedia.org/wiki/Kullback%E2%80%93Leibler_divergence)
- [Wikipedia: Jensen-Shannon Divergence](https://en.wikipedia.org/wiki/Jensen%E2%80%93Shannon_divergence)
- [Stanford IR Book: Mutual Information](https://nlp.stanford.edu/IR-book/html/htmledition/mutual-information-1.html)
- [scikit-learn: mutual_info_classif](https://scikit-learn.org/stable/modules/generated/sklearn.feature_selection.mutual_info_classif.html)
