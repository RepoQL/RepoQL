# Approximation and Sketching Algorithms

Comprehensive documentation on approximation and sketching algorithms for scalable similarity search, streaming data analysis, and efficient data structures that trade exactness for dramatic improvements in space and time complexity.

## Table of Contents

1. [Overview](#overview)
2. [Locality-Sensitive Hashing (LSH)](#locality-sensitive-hashing-lsh)
3. [MinHash and Set Similarity](#minhash-and-set-similarity)
4. [SimHash and Cosine Similarity](#simhash-and-cosine-similarity)
5. [Sketching for Frequency Estimation](#sketching-for-frequency-estimation)
6. [Bloom Filters and Variants](#bloom-filters-and-variants)
7. [Streaming Algorithms](#streaming-algorithms)
8. [Random Projections](#random-projections)
9. [Product Quantization](#product-quantization)
10. [Applications to Code Search](#applications-to-code-search)
11. [References](#references)

---

## Overview

### Why Approximation Matters

Exact algorithms for similarity search, frequency estimation, and set membership often require resources that scale poorly with data size:

```
+------------------------------------------------------------------+
|              EXACT vs APPROXIMATE TRADE-OFFS                      |
+------------------------------------------------------------------+
|                                                                   |
|  EXACT ALGORITHMS              APPROXIMATE ALGORITHMS             |
|  -----------------             ----------------------              |
|  O(n^2) similarity             O(n) with LSH                      |
|  O(n) membership               O(1) with Bloom filter             |
|  O(n) frequency                O(1) with Count-Min Sketch         |
|  Perfect accuracy              Bounded error guarantees           |
|  Terabytes of storage          Kilobytes of sketches              |
|                                                                   |
+------------------------------------------------------------------+
```

**Core Insight**: For large-scale systems, we often need to answer questions like:
- "Which documents are similar to this one?" (not: exact similarity ranking)
- "Is this item in the set?" (not: perfect membership test)
- "What is the approximate frequency?" (not: exact count)

### Approximation Paradigms

| Paradigm | Key Technique | Error Type | Applications |
|----------|---------------|------------|--------------|
| Locality-Sensitive Hashing | Hash similar items together | False positives/negatives | Near-duplicate detection, ANN |
| Sketching | Compress data to fixed-size summary | Bounded estimation error | Frequency, cardinality |
| Bloom Filters | Probabilistic set membership | False positives only | Caching, routing |
| Random Projection | Dimensionality reduction | Distance distortion | High-dimensional search |
| Quantization | Discrete approximation | Reconstruction error | Vector compression |

### Error Bounds Notation

Throughout this document, we use standard notation for probabilistic guarantees:

```
Pr[|estimate - true_value| > epsilon * true_value] < delta

Where:
  epsilon (e) = relative error bound
  delta (d)   = failure probability

Common guarantee: (1 +/- epsilon) approximation with probability 1 - delta
```

---

## Locality-Sensitive Hashing (LSH)

### Definition and Properties

Locality-Sensitive Hashing is a family of hash functions designed to maximize collisions for similar items while minimizing collisions for dissimilar items.

**Definition**: A family H is (d1, d2, p1, p2)-sensitive for distance function D if for any hash function h from H:
- If D(x, y) <= d1, then Pr[h(x) = h(y)] >= p1
- If D(x, y) >= d2, then Pr[h(x) = h(y)] <= p2

For this to be useful: d1 < d2 and p1 > p2.

```
                    Collision Probability

        1.0  +
             |    +---------+
        p1   |    |         |
             |    |  SIMILAR|
             |    |  ITEMS  |
             +....+---------+...........
             |              :
             |              :
        p2   +..............+---------+
             |              |DISSIMILAR
             |              |  ITEMS  |
        0.0  +--------------+---------+----> Distance
             0      d1      d2       inf
```

### LSH for Cosine Similarity (SimHash)

Random hyperplane hashing preserves cosine similarity between vectors.

**Hash Function**:
```
h_r(x) = sign(r . x)

Where:
  r = random vector with components from N(0, 1)
  x = input vector
  sign(z) = 1 if z >= 0, else 0
```

**Collision Probability**:
```
Pr[h_r(x) = h_r(y)] = 1 - theta(x,y) / pi

Where:
  theta(x,y) = arccos(cos_sim(x,y)) = angle between x and y
  cos_sim(x,y) = (x . y) / (||x|| ||y||)
```

**Pseudocode: SimHash for d-dimensional vectors**:
```
function SimHash(x, num_hyperplanes):
    signature = []
    for i = 1 to num_hyperplanes:
        r = RandomGaussianVector(d)
        if dot(r, x) >= 0:
            signature.append(1)
        else:
            signature.append(0)
    return signature
```

### LSH for Jaccard Similarity (MinHash)

MinHash provides LSH for the Jaccard similarity of sets.

**Collision Probability**:
```
Pr[h_pi(A) = h_pi(B)] = J(A, B) = |A intersect B| / |A union B|

Where:
  h_pi(S) = min_{x in S} pi(x)
  pi = random permutation
```

This is the fundamental theorem proven by Broder (1997).

### LSH for Euclidean Distance

For Euclidean distance, we use random projection onto lines with quantization.

**Hash Function**:
```
h_{a,b}(x) = floor((a . x + b) / w)

Where:
  a = random vector with components from N(0, 1)
  b = uniform random in [0, w]
  w = bucket width (controls sensitivity)
```

**Collision Probability** (for distance r):
```
p(r) = integral from 0 to w of (1/r) * f(t/r) * (1 - t/w) dt

Where f is the PDF of the absolute value of a standard normal.

Approximation: p(r) ~ 1 - r/w for small r/w
```

### Amplification via AND/OR Constructions

LSH families can be amplified to improve the gap between p1 and p2.

**AND-Construction**: Concatenate r hash functions; require ALL to match.
```
g(x) = (h1(x), h2(x), ..., hr(x))

New probabilities:
  p1' = p1^r  (similar items: probability decreases)
  p2' = p2^r  (dissimilar items: probability decreases faster)
```

**OR-Construction**: Use b independent hash tables; require ANY match.
```
Candidate if any of b tables has a collision.

New probabilities:
  p1'' = 1 - (1 - p1')^b  (similar items: probability increases)
  p2'' = 1 - (1 - p2')^b  (dissimilar items: probability increases slower)
```

**Combined (b, r)-LSH**:
```
+------------------------------------------------------------------+
|                    (b, r) LSH AMPLIFICATION                       |
+------------------------------------------------------------------+
|                                                                   |
|   b BANDS (OR)                                                    |
|   +---+---+---+     +---+---+---+           +---+---+---+         |
|   |h1 |h2 |...|     |h1 |h2 |...|    ...    |h1 |h2 |...|         |
|   +---+---+---+     +---+---+---+           +---+---+---+         |
|      Band 1            Band 2                  Band b             |
|                                                                   |
|   r ROWS (AND) per band                                           |
|                                                                   |
|   Collision prob: P = 1 - (1 - p^r)^b                             |
+------------------------------------------------------------------+
```

**S-Curve Effect**: The probability function becomes step-like:
```
Probability
of candidate
    |
1.0 +                           +---------
    |                       +--+
    |                    +--
    |                 +--
0.5 +. . . . . . . .X. . . . . . . . . .  threshold ~= (1/b)^(1/r)
    |             +-
    |          +--
    |      +---
0.0 +------+---------------------------------> Similarity
    0                  threshold             1
```

**Parameter Selection**:
| Goal | r (rows) | b (bands) | Effect |
|------|----------|-----------|--------|
| High precision | Large | Small | Fewer false positives |
| High recall | Small | Large | Fewer false negatives |
| Balanced | sqrt(n/k) | k | Common choice |

---

## MinHash and Set Similarity

### Jaccard Similarity

The Jaccard similarity coefficient measures the similarity between two sets:

```
J(A, B) = |A intersect B| / |A union B|

Properties:
- J(A, B) in [0, 1]
- J(A, A) = 1
- J(A, B) = 0 iff A intersect B = empty
- Symmetric: J(A, B) = J(B, A)
```

### MinHash Algorithm

MinHash efficiently estimates Jaccard similarity using random permutations.

**Core Theorem** (Broder, 1997):
```
Pr[min(pi(A)) = min(pi(B))] = J(A, B)

Intuition: The minimum element of A union B under permutation pi
is equally likely to be any element. It's in the intersection with
probability |A intersect B| / |A union B|.
```

**Pseudocode: MinHash Signature Generation**:
```
function MinHashSignature(set S, num_hashes k):
    signature = array of k infinity values

    for each element x in S:
        for i = 1 to k:
            hash_value = hash_i(x)  // k independent hash functions
            signature[i] = min(signature[i], hash_value)

    return signature
```

**Pseudocode: Jaccard Estimation**:
```
function EstimateJaccard(sig_A, sig_B):
    matches = 0
    for i = 1 to length(sig_A):
        if sig_A[i] == sig_B[i]:
            matches += 1
    return matches / length(sig_A)
```

**Error Analysis**:
```
Let X_i = 1 if sig_A[i] == sig_B[i], else 0

E[X_i] = J(A, B)
Var[X_i] = J(A, B) * (1 - J(A, B))

Estimate: J_hat = (1/k) * sum(X_i)
Standard error: SE = sqrt(J(1-J)/k)

For epsilon-accuracy with probability 1-delta:
  k >= (1 / epsilon^2) * ln(2/delta)
```

### b-Bit MinHash

Li and Konig (2010) proposed storing only the lowest b bits of each hash value.

**Space Savings**:
```
Standard MinHash: 32 or 64 bits per hash
b-bit MinHash:    b bits per hash (typically b = 1 to 8)

Compression ratio: 32/b to 64/b
```

**Modified Estimator**:
```
For 1-bit MinHash:

Pr[LSB(h(A)) = LSB(h(B))] = (J + A1*A2) / (1 + A1*A2)

Where:
  A1, A2 = expected values depending on set sizes

Unbiased estimator exists but has higher variance than full MinHash.
```

**Trade-off**:
| b | Storage | Variance | Effective threshold |
|---|---------|----------|---------------------|
| 1 | 1 bit | High | J > 0.5 works well |
| 4 | 4 bits | Medium | J > 0.2 works well |
| 8 | 8 bits | Low | J > 0.1 works well |
| 64 | 64 bits | Minimal | Any J |

### Weighted MinHash

Extension for weighted sets where elements have associated weights.

**Weighted Jaccard**:
```
J_w(A, B) = sum(min(w_A[i], w_B[i])) / sum(max(w_A[i], w_B[i]))
```

**Consistent Weighted Sampling** (Ioffe, 2010):
```
For element i with weight w_i:
  Generate (r_i, ln(c_i), beta_i) from specific distributions

  t_i = floor(ln(w_i) / r_i + beta_i)
  y_i = exp(r_i * (t_i - beta_i))
  a_i = c_i / y_i

  MinHash value = argmin_i(a_i)
```

---

## SimHash and Cosine Similarity

### Random Hyperplane Hashing

SimHash uses random hyperplanes to partition the vector space.

**Geometric Intuition**:
```
        ^
        |    * x
        |   /
   -----+--/------> hyperplane normal r
        | /
        |/ theta
        * y

Pr[sign(r.x) = sign(r.y)] = 1 - theta/pi
                          = 1 - arccos(cos_sim(x,y))/pi
```

**Pseudocode: Charikar's SimHash**:
```
function SimHash(document, num_bits):
    // Convert document to weighted term vector
    terms = tokenize(document)
    tf_idf = compute_tfidf(terms)

    // Initialize fingerprint
    v = array of num_bits zeros

    // For each term, add/subtract based on hash bits
    for term, weight in tf_idf:
        h = hash(term)  // Returns num_bits binary hash
        for i = 0 to num_bits - 1:
            if bit(h, i) == 1:
                v[i] += weight
            else:
                v[i] -= weight

    // Convert to binary fingerprint
    fingerprint = []
    for i = 0 to num_bits - 1:
        fingerprint[i] = 1 if v[i] > 0 else 0

    return fingerprint
```

**Properties**:
```
- Hamming distance between fingerprints estimates angle
- Expected Hamming distance = (num_bits / pi) * arccos(cos_sim)
- Small Hamming distance => documents likely similar
- Typical fingerprint size: 64 bits
```

### Near-Duplicate Detection with SimHash

Used by Google for web crawl deduplication (Manku et al., 2007).

**Algorithm**:
```
1. Compute SimHash fingerprint for each document
2. Build index: partition fingerprints into tables by bit positions
3. For query document:
   a. Compute its SimHash
   b. Find candidates within Hamming distance k
   c. Verify candidates with exact similarity

Key insight: If Hamming distance <= k, documents share at least
(num_bits - k) bits, which can be found by table lookup.
```

**Permutation-based Indexing**:
```
For k-bit difference tolerance with 64-bit fingerprints:

Table Layout (k=3):
  Table 1: Sort by bits [0-15]  | bits [16-63]
  Table 2: Sort by bits [16-31] | bits [0-15, 32-63]
  Table 3: Sort by bits [32-47] | bits [0-31, 48-63]
  Table 4: Sort by bits [48-63] | bits [0-47]

Any 3-bit difference must have all different bits in at least
one 16-bit block, so identical 16-bit prefix in some table.
```

---

## Sketching for Frequency Estimation

### Count-Min Sketch

Count-Min Sketch (Cormode and Muthukrishnan, 2003) estimates frequencies of elements in a data stream using sub-linear space.

**Data Structure**:
```
+------------------------------------------------------------------+
|                    COUNT-MIN SKETCH                               |
+------------------------------------------------------------------+
|                                                                   |
|  d rows (hash functions)                                          |
|  +---+---+---+---+---+---+---+---+---+---+  <- w columns          |
|  | 2 | 0 | 5 | 1 | 0 | 3 | 0 | 7 | 0 | 1 |  h1                   |
|  +---+---+---+---+---+---+---+---+---+---+                        |
|  | 0 | 3 | 1 | 0 | 4 | 0 | 2 | 0 | 6 | 0 |  h2                   |
|  +---+---+---+---+---+---+---+---+---+---+                        |
|  | 1 | 0 | 0 | 2 | 0 | 1 | 0 | 3 | 0 | 4 |  h3                   |
|  +---+---+---+---+---+---+---+---+---+---+                        |
|  ...                                                              |
|                                                                   |
|  Query(x) = min over all rows of count[row][h_row(x)]             |
+------------------------------------------------------------------+
```

**Pseudocode**:
```
function CountMinSketch:
    // Initialize
    w = ceil(e / epsilon)      // width
    d = ceil(ln(1/delta))      // depth
    count = d x w array of zeros
    h1, h2, ..., hd = independent hash functions to [0, w-1]

function Update(x, c):         // c = count increment (default 1)
    for i = 1 to d:
        count[i][h_i(x)] += c

function Query(x):
    return min(count[i][h_i(x)] for i = 1 to d)
```

**Guarantees**:
```
Let f(x) = true frequency of x
Let f_hat(x) = Query(x)

Always: f_hat(x) >= f(x)      (never underestimates)

With probability >= 1 - delta:
  f_hat(x) <= f(x) + epsilon * ||f||_1

Where ||f||_1 = sum of all frequencies = stream length n

Space: O((1/epsilon) * log(1/delta)) counters
Time: O(log(1/delta)) per update/query
```

### Count Sketch

Count Sketch (Charikar, Chen, Farach-Colton, 2002) provides unbiased frequency estimates.

**Key Difference from Count-Min**:
```
Count-Min: Always overestimates (biased)
Count Sketch: Unbiased (can over or under estimate)

Count Sketch uses sign functions:
  s_i(x) in {-1, +1} for each hash function
```

**Pseudocode**:
```
function CountSketch:
    // Initialize
    w = O(1/epsilon^2)
    d = O(log(1/delta))
    count = d x w array of zeros
    h1, ..., hd = hash functions to [0, w-1]
    s1, ..., sd = sign functions to {-1, +1}

function Update(x, c):
    for i = 1 to d:
        count[i][h_i(x)] += s_i(x) * c

function Query(x):
    estimates = [s_i(x) * count[i][h_i(x)] for i = 1 to d]
    return median(estimates)
```

**Guarantees**:
```
E[estimate] = f(x)            (unbiased)

With probability >= 1 - delta:
  |f_hat(x) - f(x)| <= epsilon * ||f||_2

Where ||f||_2 = sqrt(sum of squared frequencies)

Better for skewed distributions where few items dominate.
```

### Applications: Term Frequency Estimation

**Use Case**: Estimate term frequencies in large document collections without storing full inverted index.

```
Document Stream Processing:

for each document D:
    for each term t in D:
        sketch.Update(t, tf(t, D))

Query: "What is the total frequency of term 'authentication'?"
Answer: sketch.Query('authentication')

Space: O(1/epsilon * log(1/delta)) instead of O(vocabulary_size)
```

---

## Bloom Filters and Variants

### Standard Bloom Filter

A Bloom filter (Bloom, 1970) is a space-efficient probabilistic data structure for set membership testing.

**Data Structure**:
```
+------------------------------------------------------------------+
|                    BLOOM FILTER                                   |
+------------------------------------------------------------------+
|                                                                   |
|  Bit array of m bits, initially all 0                             |
|  +---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+    |
|  | 0 | 1 | 0 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 1 | 0 | 1 | 0 | 1 |    |
|  +---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+    |
|    ^       ^   ^                   ^       ^                      |
|    |       |   |                   |       |                      |
|    h1(x)   h2(x) h3(x)           h1(y)   h2(y)                   |
|                                                                   |
|  Insert(x): Set bits at h1(x), h2(x), ..., hk(x) to 1            |
|  Query(x):  Return AND of bits at h1(x), h2(x), ..., hk(x)       |
+------------------------------------------------------------------+
```

**Pseudocode**:
```
function BloomFilter(m, k):
    bits = array of m zeros
    h1, ..., hk = independent hash functions to [0, m-1]

function Insert(x):
    for i = 1 to k:
        bits[h_i(x)] = 1

function Query(x):
    for i = 1 to k:
        if bits[h_i(x)] == 0:
            return False  // Definitely not in set
    return True           // Probably in set
```

**False Positive Probability**:
```
After inserting n elements:

Pr[bit is 0] ~ (1 - 1/m)^(kn) ~ e^(-kn/m)

Pr[false positive] ~ (1 - e^(-kn/m))^k

Optimal k: k = (m/n) * ln(2) ~ 0.693 * (m/n)

With optimal k:
  FPP ~ (0.6185)^(m/n)

For 1% FPP: m/n ~ 9.6 bits per element
For 0.1% FPP: m/n ~ 14.4 bits per element
```

**Space Comparison**:
| Data Structure | Space per element | False positives |
|----------------|------------------|-----------------|
| Hash set | 32-64+ bits | None |
| Bloom filter (1% FPP) | 9.6 bits | 1% |
| Bloom filter (0.1% FPP) | 14.4 bits | 0.1% |
| Optimal bound | ln(1/FPP)/ln(2) bits | As specified |

### Counting Bloom Filter

Extends Bloom filters to support deletion by using counters instead of bits.

**Pseudocode**:
```
function CountingBloomFilter(m, k):
    counters = array of m zeros (typically 4 bits each)

function Insert(x):
    for i = 1 to k:
        counters[h_i(x)] += 1

function Delete(x):
    for i = 1 to k:
        counters[h_i(x)] -= 1  // Must not go negative

function Query(x):
    return min(counters[h_i(x)] for i = 1 to k) > 0
```

**Drawbacks**:
- 4x space overhead (4-bit counters)
- Counter overflow possible with many insertions
- False negatives possible if delete item not in set

### Cuckoo Filter

Cuckoo filters (Fan et al., 2014) offer better space efficiency and support deletion.

**Structure**:
```
+------------------------------------------------------------------+
|                    CUCKOO FILTER                                  |
+------------------------------------------------------------------+
|                                                                   |
|  Array of buckets, each holding b fingerprints                    |
|                                                                   |
|  Bucket 0:  [fp1] [fp2] [   ] [   ]                              |
|  Bucket 1:  [fp3] [   ] [   ] [   ]                              |
|  Bucket 2:  [fp4] [fp5] [fp6] [   ]                              |
|  ...                                                              |
|                                                                   |
|  For item x:                                                      |
|    fingerprint = hash(x) mod 2^f                                  |
|    bucket1 = hash(x) mod num_buckets                              |
|    bucket2 = bucket1 XOR hash(fingerprint)  // partial-key cuckoo |
+------------------------------------------------------------------+
```

**Pseudocode**:
```
function Insert(x):
    fp = fingerprint(x)
    b1 = hash(x) mod num_buckets
    b2 = b1 XOR hash(fp)

    if bucket[b1] has empty slot:
        add fp to bucket[b1]
        return true
    if bucket[b2] has empty slot:
        add fp to bucket[b2]
        return true

    // Must relocate existing items
    b = randomly choose b1 or b2
    for i = 1 to MAX_KICKS:
        swap fp with random entry in bucket[b]
        b = b XOR hash(fp)
        if bucket[b] has empty slot:
            add fp to bucket[b]
            return true
    return false  // Filter is full

function Query(x):
    fp = fingerprint(x)
    b1 = hash(x) mod num_buckets
    b2 = b1 XOR hash(fp)
    return fp in bucket[b1] or fp in bucket[b2]

function Delete(x):
    fp = fingerprint(x)
    b1 = hash(x) mod num_buckets
    b2 = b1 XOR hash(fp)
    if fp in bucket[b1]: remove from bucket[b1]; return true
    if fp in bucket[b2]: remove from bucket[b2]; return true
    return false
```

**Comparison**:
| Property | Bloom | Counting Bloom | Cuckoo |
|----------|-------|----------------|--------|
| Deletion | No | Yes | Yes |
| Space (3% FPP) | ~10 bits/item | ~40 bits/item | ~7 bits/item |
| Lookup | O(k) | O(k) | O(1) |
| Insert | O(k) | O(k) | O(1) amortized |
| False positive | Tunable | Tunable | Tunable |

---

## Streaming Algorithms

### Heavy Hitters: Misra-Gries Algorithm

Find elements appearing more than n/k times in a stream of length n.

**Pseudocode**:
```
function MisraGries(stream, k):
    counters = empty map (at most k-1 entries)

    for each element x in stream:
        if x in counters:
            counters[x] += 1
        else if |counters| < k - 1:
            counters[x] = 1
        else:
            // Decrement all counters, remove zeros
            for each y in counters:
                counters[y] -= 1
                if counters[y] == 0:
                    remove y from counters

    return counters

// Second pass to get exact counts (optional)
function VerifyHeavyHitters(stream, candidates, threshold):
    exact_counts = count each candidate in stream
    return {x : exact_counts[x] > threshold}
```

**Guarantees**:
```
Space: O(k) counters

For any element x with true frequency f(x):
  counter[x] >= f(x) - n/k

All elements with f(x) > n/k are guaranteed to be in output.
Some elements with f(x) <= n/k may also appear.
```

### Heavy Hitters: Space-Saving Algorithm

Space-Saving (Metwally et al., 2005) maintains better estimates than Misra-Gries.

**Pseudocode**:
```
function SpaceSaving(stream, k):
    counters = map of k items to (count, error) pairs

    for each element x in stream:
        if x in counters:
            counters[x].count += 1
        else if |counters| < k:
            counters[x] = (count=1, error=0)
        else:
            // Replace minimum element
            min_item = argmin(counters[y].count for y in counters)
            min_count = counters[min_item].count
            remove min_item from counters
            counters[x] = (count=min_count+1, error=min_count)

    return counters
```

**Properties**:
```
For element x with counter (count, error):
  True frequency f(x) in [count - error, count]

Space: O(k) entries
All elements with f(x) > n/k are in output with count > n/k.
```

### Distinct Count: HyperLogLog

HyperLogLog (Flajolet et al., 2007) estimates the number of distinct elements in a stream.

**Core Idea**:
```
Observation: If we hash n distinct elements uniformly to [0, 2^L):
  Expected maximum number of leading zeros ~ log2(n)

By tracking the maximum leading zeros seen, we estimate cardinality.
```

**Pseudocode**:
```
function HyperLogLog(stream, b):
    m = 2^b           // number of registers
    M = array of m zeros  // registers
    alpha = correction constant based on m

    for each element x in stream:
        h = hash(x)           // L-bit hash
        j = first b bits of h // register index
        w = remaining bits of h
        M[j] = max(M[j], leading_zeros(w) + 1)

    // Raw estimate
    Z = 1 / sum(2^(-M[j]) for j = 0 to m-1)
    E = alpha * m^2 * Z

    // Bias correction for small/large cardinalities
    if E <= 2.5 * m:
        V = count of M[j] == 0
        if V > 0: E = m * ln(m/V)  // Linear counting
    else if E > 2^32 / 30:
        E = -2^32 * ln(1 - E/2^32)

    return E
```

**Guarantees**:
```
Standard error: 1.04 / sqrt(m)

For m = 2^10 = 1024 registers:
  Space: ~1.5 KB (6 bits per register)
  Error: ~3.25%

For m = 2^14 = 16384 registers:
  Space: ~12 KB
  Error: ~0.81%
```

**HyperLogLog++ Improvements** (Google, 2013):
- 64-bit hashes (reduced collision for large cardinalities)
- Empirical bias correction (better accuracy for small cardinalities)
- Sparse representation (memory efficient for small sets)

### Quantile Estimation

Estimate approximate quantiles (median, percentiles) in streaming data.

**Greenwald-Khanna Algorithm** (2001):
```
Maintains a summary S of tuples (v, g, delta) where:
  v = sampled value
  g = difference in rank from predecessor
  delta = uncertainty in rank

Guarantees epsilon-approximate quantile:
  |rank(v) - true_rank(phi * n)| <= epsilon * n

Space: O((1/epsilon) * log(epsilon * n))
```

**t-Digest** (Dunning, 2013):
```
Maintains centroids with adaptive compression:
- More accurate at extreme quantiles (p99, p999)
- Merges clusters using size function based on quantile

Space: O(1/delta) where delta controls accuracy
Typical: ~10 KB for 1% accuracy on p99
```

---

## Random Projections

### Johnson-Lindenstrauss Lemma

The JL lemma states that high-dimensional points can be embedded in low dimensions while approximately preserving distances.

**Theorem** (Johnson-Lindenstrauss, 1984):
```
For any epsilon in (0, 1) and any set P of n points in R^d,
there exists a map f: R^d -> R^k where k = O(log(n)/epsilon^2)
such that for all u, v in P:

(1 - epsilon) * ||u - v||^2 <= ||f(u) - f(v)||^2 <= (1 + epsilon) * ||u - v||^2
```

**Key Insight**: The target dimension k depends only on n and epsilon, NOT on the original dimension d.

**Dimension Formula**:
```
k >= (4 + 2*beta) / (epsilon^2/2 - epsilon^3/3) * ln(n)

Simplified: k >= 8 * ln(n) / epsilon^2

Example:
  n = 1,000,000 points
  epsilon = 0.1 (10% distortion)
  k >= 8 * ln(10^6) / 0.01 = 8 * 13.8 / 0.01 ~ 11,000 dimensions

  Reduce from d = 10,000,000 to k = 11,000: 1000x reduction
```

### Random Projection Matrices

**Gaussian Random Projection**:
```
R = (1/sqrt(k)) * G

Where G is k x d matrix with entries from N(0, 1)

Projected point: x' = R * x

Properties:
- E[||Rx||^2] = ||x||^2
- Var[||Rx||^2] decreases with k
```

**Sparse Random Projection** (Achlioptas, 2003):
```
R_ij = sqrt(3/k) * { +1 with prob 1/6
                   {  0 with prob 2/3
                   { -1 with prob 1/6

Benefits:
- 3x faster to compute (2/3 entries are zero)
- Same JL guarantees
- Database-friendly (only -1, 0, +1)
```

**Very Sparse Random Projection** (Li, Hastie, Church, 2006):
```
R_ij = sqrt(s/k) * { +1 with prob 1/(2s)
                   {  0 with prob 1 - 1/s
                   { -1 with prob 1/(2s)

With s = sqrt(d), only O(sqrt(d)) non-zeros per column.
Further speedup for very high dimensions.
```

### Application: Dimensionality Reduction

**Pseudocode**:
```
function RandomProjection(X, target_dim, method='sparse'):
    n, d = X.shape
    k = target_dim

    if method == 'gaussian':
        R = random_normal(k, d) / sqrt(k)
    elif method == 'sparse':
        R = sparse_rademacher(k, d, density=1/3) * sqrt(3/k)

    return X @ R.T  // n x k matrix
```

**Time Complexity**:
| Method | Projection Time | Notes |
|--------|-----------------|-------|
| Gaussian | O(ndk) | Dense matrix multiply |
| Sparse (s=3) | O(ndk/3) | 2/3 zeros |
| Very sparse (s=sqrt(d)) | O(nk*sqrt(d)) | Almost all zeros |
| Fast JL (Ailon-Chazelle) | O(nd log k) | FFT-based |

---

## Product Quantization

### Vector Compression for ANN

Product Quantization (Jegou et al., 2011) compresses vectors by decomposing them into subvectors and quantizing each independently.

**Core Idea**:
```
+------------------------------------------------------------------+
|                    PRODUCT QUANTIZATION                           |
+------------------------------------------------------------------+
|                                                                   |
|  Original vector x in R^D:                                        |
|  [x1, x2, x3, x4, x5, x6, x7, x8, ..., xD]                       |
|                                                                   |
|  Split into M subvectors:                                         |
|  [x1..xd] [xd+1..x2d] ... [x(M-1)d+1..xD]                        |
|     |         |                |                                  |
|     v         v                v                                  |
|  Quantize each subvector to nearest centroid:                     |
|  [c1_23]  [c2_156]  ...   [cM_42]                                |
|                                                                   |
|  Store only centroid IDs (log2(K) bits each):                    |
|  Code: [23, 156, ..., 42]  <- M integers, each in [0, K-1]       |
+------------------------------------------------------------------+
```

**Training**:
```
function TrainPQ(vectors X, num_subquantizers M, num_centroids K):
    D = dimension of vectors
    d = D / M  // subvector dimension

    codebooks = []
    for m = 1 to M:
        subvectors = X[:, (m-1)*d : m*d]  // extract subvectors
        centroids = KMeans(subvectors, K)
        codebooks.append(centroids)

    return codebooks

function Encode(x, codebooks):
    code = []
    for m = 1 to M:
        subvector = x[(m-1)*d : m*d]
        nearest = argmin_k ||subvector - codebooks[m][k]||
        code.append(nearest)
    return code
```

**Compression Ratio**:
```
Original: D * 32 bits (float32)
PQ code: M * log2(K) bits

Example:
  D = 768 dimensions
  M = 96 subquantizers
  K = 256 centroids (8 bits each)

  Original: 768 * 32 = 24,576 bits = 3,072 bytes
  PQ code: 96 * 8 = 768 bits = 96 bytes

  Compression: 32x
```

### Asymmetric Distance Computation (ADC)

Compute distances between original query and compressed database vectors.

```
function ADC_Distance(query q, code c, codebooks):
    // Precompute distances from query to all centroids
    distance_tables = []
    for m = 1 to M:
        subquery = q[(m-1)*d : m*d]
        table = [||subquery - codebooks[m][k]||^2 for k in 0..K-1]
        distance_tables.append(table)

    // Distance computation is just table lookups
    dist_sq = sum(distance_tables[m][c[m]] for m = 1 to M)
    return sqrt(dist_sq)
```

**Time Complexity**:
```
Precomputation: O(M * K * d) per query
Distance computation: O(M) per database vector

Compare to brute force: O(D) per vector
Speedup: D/M = d (subvector dimension)
```

### Connection to HNSW Indices

HNSW with PQ combines graph-based search with vector compression.

**HNSW-PQ Pipeline**:
```
1. Train PQ codebook on dataset
2. Encode all vectors to PQ codes
3. Build HNSW graph on original vectors (or PQ codes)
4. At query time:
   a. Compute distance tables once
   b. Navigate HNSW using ADC distances
   c. Rerank top candidates with original vectors
```

**IVF-PQ (Inverted File with PQ)**:
```
+------------------------------------------------------------------+
|                       IVF-PQ STRUCTURE                            |
+------------------------------------------------------------------+
|                                                                   |
|  Coarse quantizer: K_coarse centroids                             |
|  Each centroid -> inverted list of PQ codes                       |
|                                                                   |
|  Centroid 0: [pq_code_5, pq_code_23, pq_code_107, ...]           |
|  Centroid 1: [pq_code_2, pq_code_89, ...]                        |
|  ...                                                              |
|  Centroid K: [...]                                                |
|                                                                   |
|  Query:                                                           |
|  1. Find nprobe nearest coarse centroids                          |
|  2. Scan PQ codes in those lists only                            |
|  3. Return top-k based on ADC distances                          |
+------------------------------------------------------------------+
```

---

## Applications to Code Search

### Near-Duplicate Detection

**Use Case**: Identify copied/pasted code, forked files, or refactored code.

**SimHash Approach**:
```
function CodeSimHash(code_file):
    // Tokenize code
    tokens = lexer(code_file)

    // Generate features
    features = {}
    for i = 0 to len(tokens) - 3:
        trigram = (tokens[i], tokens[i+1], tokens[i+2])
        features[trigram] = features.get(trigram, 0) + 1

    // Compute SimHash
    return SimHash(features, num_bits=64)

function FindNearDuplicates(codebase, threshold=3):
    // threshold = max Hamming distance
    hashes = {f: CodeSimHash(f) for f in codebase}

    // Build LSH index for efficient search
    lsh = LSHIndex(num_bands=16, rows_per_band=4)
    for f, h in hashes:
        lsh.insert(f, h)

    // Find candidate pairs
    duplicates = []
    for f, h in hashes:
        candidates = lsh.query(h)
        for c in candidates:
            if hamming_distance(h, hashes[c]) <= threshold:
                duplicates.append((f, c))

    return duplicates
```

**MinHash for Structural Similarity**:
```
function CodeMinHash(ast):
    // Extract structural features from AST
    features = set()
    for node in ast.walk():
        features.add((node.type, node.parent.type, node.depth))
        features.add(subtree_hash(node, depth=2))

    return MinHashSignature(features, num_hashes=128)
```

### Efficient Filtering

**Bloom Filter for Symbol Lookup**:
```
function BuildSymbolFilter(codebase):
    bf = BloomFilter(expected_items=1000000, fpp=0.01)

    for file in codebase:
        for symbol in extract_symbols(file):
            bf.Insert(symbol)

    return bf

function QuickSymbolCheck(query, filter):
    // Fast negative check before expensive search
    if not filter.Query(query):
        return []  // Definitely not in codebase

    // May be present, do full search
    return full_search(query)
```

**Cuckoo Filter for Dynamic Codebases**:
```
function IncrementalSymbolIndex(codebase):
    cf = CuckooFilter(capacity=1000000, fpp=0.01)

    // Initial population
    for file in codebase:
        for symbol in extract_symbols(file):
            cf.Insert(symbol)

    // On file change
    function on_file_delete(file):
        for symbol in extract_symbols(file):
            cf.Delete(symbol)

    function on_file_add(file):
        for symbol in extract_symbols(file):
            cf.Insert(symbol)
```

### Streaming Index Updates

**Count-Min Sketch for Term Importance**:
```
function StreamingTFEstimator():
    cms = CountMinSketch(width=10000, depth=5)
    doc_count = HyperLogLog(precision=14)

    function process_document(doc):
        doc_count.add(doc.id)
        for term in tokenize(doc):
            cms.Update(term, 1)

    function estimate_idf(term):
        tf = cms.Query(term)
        n = doc_count.estimate()
        return log(n / (tf + 1))
```

**HyperLogLog for Repository Statistics**:
```
function RepoStats():
    unique_symbols = HyperLogLog(precision=12)
    unique_files = HyperLogLog(precision=10)
    commit_authors = HyperLogLog(precision=8)

    function process_file(file):
        unique_files.add(file.path)
        for symbol in extract_symbols(file):
            unique_symbols.add(symbol)

    function summary():
        return {
            'estimated_symbols': unique_symbols.estimate(),
            'estimated_files': unique_files.estimate(),
            'estimated_authors': commit_authors.estimate()
        }
```

### Sketch-Based Similarity for Code Embeddings

**Product Quantization for Embedding Storage**:
```
function CodeEmbeddingIndex(codebase, embedding_model):
    // Generate embeddings
    embeddings = {}
    for file in codebase:
        embeddings[file] = embedding_model.encode(file.content)

    // Train PQ
    D = embedding_dimension  // e.g., 384
    M = 48                   // subquantizers
    K = 256                  // centroids per subquantizer

    pq = TrainPQ(list(embeddings.values()), M, K)

    // Encode and store
    codes = {f: pq.encode(e) for f, e in embeddings.items()}

    // Storage: 48 bytes per file instead of 1536 bytes (32x compression)

    function search(query, k=10):
        query_embedding = embedding_model.encode(query)
        distances = {f: pq.adc_distance(query_embedding, c)
                     for f, c in codes.items()}
        return top_k(distances, k)
```

---

## References

### Foundational Papers

1. **Locality-Sensitive Hashing**: Indyk, P. and Motwani, R. (1998). [Approximate Nearest Neighbors: Towards Removing the Curse of Dimensionality](https://www.cs.princeton.edu/courses/archive/spr04/cos598B/bib/IndykM-curse.pdf). STOC'98.

2. **MinHash**: Broder, A. Z. (1997). [On the Resemblance and Containment of Documents](http://www.cs.princeton.edu/courses/archive/spr04/cos598B/bib/BrosijI1.pdf). Compression and Complexity of SEQUENCES.

3. **SimHash**: Charikar, M. (2002). [Similarity Estimation Techniques from Rounding Algorithms](https://www.cs.princeton.edu/courses/archive/spr04/cos598B/bib/CharikarEstim.pdf). STOC'02.

4. **Count-Min Sketch**: Cormode, G. and Muthukrishnan, S. (2005). [An Improved Data Stream Summary: The Count-Min Sketch and its Applications](https://dimacs.rutgers.edu/~graham/pubs/papers/cm-full.pdf). Journal of Algorithms.

5. **Count Sketch**: Charikar, M., Chen, K., and Farach-Colton, M. (2002). [Finding Frequent Items in Data Streams](https://www.cs.princeton.edu/courses/archive/spr04/cos598B/bib/CharikarCF.pdf). ICALP'02.

6. **Bloom Filter**: Bloom, B. H. (1970). [Space/Time Trade-offs in Hash Coding with Allowable Errors](https://dl.acm.org/doi/10.1145/362686.362692). Communications of the ACM.

7. **Cuckoo Filter**: Fan, B., Andersen, D. G., Kaminsky, M., and Mitzenmacher, M. (2014). [Cuckoo Filter: Practically Better Than Bloom](https://www.cs.cmu.edu/~dga/papers/cuckoo-conext2014.pdf). CoNEXT'14.

8. **HyperLogLog**: Flajolet, P., Fusy, E., Gandouet, O., and Meunier, F. (2007). [HyperLogLog: The Analysis of a Near-Optimal Cardinality Estimation Algorithm](https://algo.inria.fr/flajolet/Publications/FlFuGaMe07.pdf). AofA'07.

9. **Misra-Gries**: Misra, J. and Gries, D. (1982). [Finding Repeated Elements](https://www.cs.utexas.edu/users/misra/scannedPdf.dir/FindsRepeatedElements.pdf). Science of Computer Programming.

10. **Space-Saving**: Metwally, A., Agrawal, D., and El Abbadi, A. (2005). [Efficient Computation of Frequent and Top-k Elements in Data Streams](https://www.cs.ucsb.edu/sites/default/files/documents/2005-23.pdf). ICDT'05.

11. **Johnson-Lindenstrauss**: Johnson, W. B. and Lindenstrauss, J. (1984). Extensions of Lipschitz Mappings into a Hilbert Space. Contemporary Mathematics.

12. **Sparse Random Projections**: Achlioptas, D. (2003). [Database-friendly Random Projections: Johnson-Lindenstrauss with Binary Coins](https://www.sciencedirect.com/science/article/pii/S0022000003000254). Journal of Computer and System Sciences.

13. **Product Quantization**: Jegou, H., Douze, M., and Schmid, C. (2011). [Product Quantization for Nearest Neighbor Search](https://inria.hal.science/inria-00514462v2/document). IEEE TPAMI.

14. **b-bit MinHash**: Li, P. and Konig, A. C. (2010). [b-Bit Minwise Hashing](https://dl.acm.org/doi/10.1145/1772690.1772759). WWW'10.

### Additional Resources

- [Wikipedia: Locality-sensitive hashing](https://en.wikipedia.org/wiki/Locality-sensitive_hashing)
- [Wikipedia: MinHash](https://en.wikipedia.org/wiki/MinHash)
- [Wikipedia: Count-min sketch](https://en.wikipedia.org/wiki/Count–min_sketch)
- [Wikipedia: HyperLogLog](https://en.wikipedia.org/wiki/HyperLogLog)
- [Wikipedia: Bloom filter](https://en.wikipedia.org/wiki/Bloom_filter)
- [Wikipedia: Cuckoo filter](https://en.wikipedia.org/wiki/Cuckoo_filter)
- [Wikipedia: Random projection](https://en.wikipedia.org/wiki/Random_projection)
- [Pinecone: Locality Sensitive Hashing Guide](https://www.pinecone.io/learn/series/faiss/locality-sensitive-hashing/)
- [Pinecone: Product Quantization Guide](https://www.pinecone.io/learn/series/faiss/product-quantization/)
- [Redis: Count-Min Sketch Guide](https://redis.io/blog/count-min-sketch-the-art-and-science-of-estimating-stuff/)
- [Google: HyperLogLog in Practice](https://research.google.com/pubs/archive/40671.pdf)
- [Google: Detecting Near-Duplicates for Web Crawling](https://research.google.com/pubs/archive/33026.pdf)
- [CMU: Cuckoo Filter Paper](https://www.cs.cmu.edu/~dga/papers/cuckoo-conext2014.pdf)

### DuckDB Resources

- [DuckDB: Array Functions](https://duckdb.org/docs/sql/functions/nested.html)
- [DuckDB: Vector Similarity Search Extension](https://duckdb.org/docs/stable/core_extensions/vss)

### Scikit-learn Documentation

- [Random Projection in scikit-learn](https://scikit-learn.org/stable/modules/random_projection.html)
- [Johnson-Lindenstrauss Bound Examples](https://scikit-learn.org/stable/auto_examples/miscellaneous/plot_johnson_lindenstrauss_bound.html)

---

*Document version: 1.0 | Last updated: January 2026*
