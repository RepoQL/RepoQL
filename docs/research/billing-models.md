---
description: Billing model research for RepoQL cloud services — seat-based, usage-based, hybrid, and credit models evaluated against actual costs and industry patterns.
tags: [billing, pricing, stripe, saas, cloud-cache]
audience: { human: 70, agent: 30 }
purpose: { research: 90, design: 10 }
---

# Billing Models for RepoQL Cloud Services

Research for selecting a billing model for RepoQL's paid cloud features (embedding cache, explain, reranking).

*Research date: March 9, 2026*

## Context

RepoQL core is free — structural queries, local semantic search, JIT embedding all work without an account. Cloud features (explain via LLM, reranking via Voyage, cloud embedding cache) require a paid account.

The existing design ([auth-and-billing.md](../flows/future/llm-service/auth-and-billing.md)) proposes:

| Plan | Price (Marketplace) | Price (Stripe) | Explain | Reranking | Cloud Embedding |
|------|--------------------|--------------:|--------:|----------:|----------------:|
| **Pro** | $12/month | $10/month | 2,000/month | Included | Unlimited |
| **Team** | $30/seat/month | $25/seat/month | Unlimited | Included | Unlimited |

This research evaluates whether this structure is optimal, what alternatives exist, and what the industry has converged on.

**Constraint: no per-repo limits.** Importing repos (`github://lodash/lodash`, `github://org/internal-lib`) should be frictionless — no mental accounting about whether a repo is "worth a slot." The cloud cache makes this economically viable: embedding is a one-time cost (~$1.80-2.70/repo), amortized across all customers who import the same repo. The marginal cost of a user's 6th or 60th repo approaches zero for popular repos.

**Note:** The existing design states GitHub Marketplace takes a 25% cut. Current Marketplace fee is **5%** (reduced from 25% in January 2021). This changes the margin math — Marketplace and Stripe pricing can be closer together.

> [GitHub Marketplace fee reduction](https://github.blog/news-insights/company-news/github-reduces-marketplace-transaction-fees-revamps-technology-partner-program/) — 5% since January 2021

---

## RepoQL's Cost Structure

Understanding the cost floor is prerequisite to evaluating billing models.

### Per-Feature API Costs

| Feature | Provider | Cost per unit | Typical usage | Monthly cost (heavy user) |
|---------|----------|--------------|---------------|--------------------------|
| Explain | xAI Grok 4.1 Fast | $0.20/M input, $0.50/M output | ~5K tokens/call | ~$0.0013/call |
| Reranking | Voyage rerank-2 | $0.05/M tokens | ~5K tokens/search | ~$0.00025/search |
| Embedding | Voyage voyage-context-3 | $0.18/M tokens | One-time per repo | ~$1.80-2.70/full index |

> [Voyage AI pricing](https://docs.voyageai.com/docs/pricing) — embedding and reranking rates
> [xAI pricing](https://docs.x.ai/developers/models) — Grok 4.1 Fast rates

### Key Cost Characteristics

**Explain is the largest variable cost** but cheap per call. 2,000 calls/month (Pro cap) costs ~$2.60 in API fees. This is well within a $10/mo plan.

**Reranking is essentially free.** At $0.00025/search, even 200 searches/month costs $0.05. Not worth metering.

**Embedding is a one-time cost per repo, amortized by the cloud cache.** The cache design means a repo is embedded once, then served to all customers working on that repo. Five developers on the same repo: embedding cost is 1/5th per person. Popular open-source imports (`github://lodash/lodash`) are embedded once, served to everyone.

**GCP infrastructure is negligible.** Cloud Run free tier covers early usage. At scale, the embedding service costs ~$5-15/month, GCS storage for 100M cached embeddings is ~$2/month, Cloud Tasks is pennies.

### Margin Analysis

| Scenario | Revenue | Costs | Margin |
|----------|---------|-------|--------|
| Pro user, maxes explain (2,000 calls) | $10/mo (Stripe) | ~$4.30 | ~57% |
| Pro user, moderate (200 calls) | $10/mo (Stripe) | ~$1.08 | ~89% |
| Team seat, heavy (1,000 calls) | $25/mo (Stripe) | ~$3.20 | ~87% |
| Team seat, cache-shared (5-person team) | $25/mo (Stripe) | ~$2.30 | ~91% |

Stripe fees: 3.6% + $0.30 per transaction. GitHub Marketplace: 5%.

> [Stripe pricing](https://stripe.com/pricing) — 2.9% + $0.30 processing, +0.7% for Billing

**The existing plan structure is sustainable.** Even worst-case Pro users leave ~57% margin. The cloud cache is the primary margin driver — it converts a per-user cost into a shared cost.

### Cost Ceiling: Protecting Against Abuse

Per-repo limits are undesirable (they create friction at the point of exploration), but "unlimited" with no ceiling exposes the service to cost explosion. A user importing thousands of obscure private repos forces fresh Voyage API calls for each — no cache hits, no amortization.

The actual cost driver is **embedding token volume**, not repo count. Possible ceiling mechanisms:

| Mechanism | How it works | User experience | Vendor protection |
|-----------|-------------|-----------------|-------------------|
| **Embedding token budget** | Monthly cap on embedding tokens consumed (e.g., 10M tokens/month). Cache hits don't count — only fresh Voyage calls. | Invisible for normal use. Power users see "embedding budget used — resets next month" | Direct cost ceiling on the expensive operation |
| **Concurrent repo limit** | Cap on how many repos can be actively indexed at once. Archived/cached repos don't count. | "You can have N repos actively indexing" — less friction than total repo count | Limits concurrent embedding load, not total repos ever imported |
| **Fair use with monitoring** | No hard cap. Monitor per-user embedding spend. Alert/throttle outliers beyond a multiple of median usage | Invisible unless abusing. Feels unlimited | Catches abuse without penalizing normal use |
| **Explain call cap (existing)** | Already proposed: 2,000 explain calls/month on Pro | Clear, simple limit on the largest variable cost | Directly caps the most expensive per-call feature |
| **Total monthly API cost cap** | Internal per-user cost ceiling (e.g., $5/user/month in API spend). Throttle when exceeded | User sees "cloud features temporarily limited — resets next month" | Universal protection against any cost vector |

The cloud cache changes the economics here: most imports of public repos are cache hits (zero Voyage cost). The risk is concentrated in **private repos with no other users** — each one is guaranteed to be a cache miss on first index. A ceiling on fresh embedding tokens per month would be invisible to most users while protecting against the pathological case.

For context, a typical 10K-file repo costs ~5M embedding tokens. A 10M token/month budget allows ~2 fresh large repos/month — more than enough for normal development. Popular public repos cost nothing (cache hits).

---

## What the Industry Ships

### Developer AI Tool Pricing (March 2026)

| Tool | Model | Individual | Team/Business | Free Tier |
|------|-------|-----------|---------------|-----------|
| GitHub Copilot | Seat + overage | $10-39/mo (300-1,500 requests) | $19-39/user/mo | 50 requests + 2K completions |
| Cursor | Seat + credit pool | $20/mo ($20 credit pool) | $40/user/mo | 50 slow requests |
| Windsurf | Seat + credits | $15/mo (500 credits) | $30/user/mo | 25 credits |
| Tabnine | Flat per-seat | $12/user/mo | $39/user/mo | Basic local completions |
| Amazon Q | Flat per-seat | Free (perpetual) | $19/user/mo | 50 agentic + unlimited completions |
| JetBrains AI | Seat + credits | $8-30/mo (8-35 credits) | Contact sales | 3 credits + local completion |
| Augment Code | Credit pool | $20/mo (40K credits) | $60/mo (up to 20 users) | Discontinued |
| Cody (Sourcegraph) | Flat per-seat | Discontinued | $59/user/mo (25 min) | Discontinued |

> [GitHub Copilot plans](https://github.com/features/copilot/plans) — seat + premium request overage at $0.04/request
> [Cursor pricing](https://cursor.com/pricing) — credit pool model since August 2025
> [Windsurf pricing](https://windsurf.com/pricing) — credit-based since Codeium rebrand

### The Industry Trend

The market moved away from pure flat-rate per-seat toward **hybrid (seat + usage)** through 2025:

- GitHub Copilot introduced metered overage billing (June 2025)
- Cursor moved from request caps to API-cost credit pools (August 2025)
- Augment Code moved from message counts to credits (October 2025)
- JetBrains launched 1-credit-per-dollar system (2025)

45% of public SaaS companies now use hybrid pricing, up from 23% five years ago.

> [Chargebee hybrid pricing analysis](https://www.chargebee.com/blog/hybrid-pricing-model-in-saas/) — 45% adoption
> [Maxio hybrid pricing report](https://www.maxio.com/blog/the-rise-of-hybrid-pricing-models) — highest median growth rate (21%)

### Individual price range: $8-20/month. Enterprise: $19-60/user/month.

---

## Billing Model Evaluation

### Seat-Based (Flat Rate)

A fixed monthly fee per user.

| Advantage | Detail |
|-----------|--------|
| Simple | Easy to understand, budget, and implement |
| Predictable revenue | Stable MRR, straightforward forecasting |
| Low implementation cost | No metering infrastructure |

| Disadvantage | Detail |
|--------------|--------|
| Shelfware | 50% of software licenses go unused across the industry. Teams resist adding seats because it's "all or nothing" |
| AI asymmetry | One user may consume 10-20x more compute than another but pays the same |
| No expansion revenue | No natural path to grow revenue without adding seats |

> [Vertice shelfware research](https://www.vertice.one/blog/saas-wastage-shelfware) — 50% license waste
> [Bain seat-based analysis](https://www.bain.com/insights/per-seat-software-pricing-isnt-dead-but-new-models-are-gaining-steam/) — 65% still use seats, but layering usage on top

### Usage-Based (Pure)

Pay per API call, token, or action.

| Advantage | Detail |
|-----------|--------|
| Cost-value alignment | Customers pay for what they use |
| Low barrier to entry | Start small, expand naturally |
| Higher NRR | Usage-based companies grow ~2x faster; Snowflake achieved 158% NDR |

| Disadvantage | Detail |
|--------------|--------|
| Bill shock | 78% of IT leaders experienced unexpected AI charges in 2025 |
| Revenue unpredictability | Harder to forecast for both vendor and buyer |
| Self-throttling | Customers may limit usage to control costs, reducing value received |

> [Zylo 2026 SaaS Management Index](https://zylo.com/reports/2026-saas-management-index/) — 78% unexpected charges
> [OpenView usage-based benchmarks](https://openviewpartners.com/usage-based-pricing/) — 2x growth advantage

### Hybrid (Base + Usage)

Fixed subscription floor with usage-based component for variable costs.

| Advantage | Detail |
|-----------|--------|
| Best of both | Predictable floor for buyer, expansion capture for vendor |
| Highest growth | Reports the highest median growth rate (21%) among models |
| Matches AI economics | Base covers platform costs; usage covers API costs that scale linearly |

| Disadvantage | Detail |
|--------------|--------|
| Complexity risk | Datadog is the cautionary tale — multi-dimensional pricing creates billing anxiety |
| Requires metering | Must build usage tracking and reporting infrastructure |

> [Bessemer AI pricing playbook](https://www.bvp.com/atlas/the-ai-pricing-and-monetization-playbook) — "effective middle ground for early-stage startups"
> [Datadog pricing criticism](https://signoz.io/blog/datadog-pricing/) — complexity anti-pattern

### Credits / Token Models

Pre-purchased credits consumed by usage. 1 credit = $1 (JetBrains) or 1 credit = 1 action (Windsurf).

| Advantage | Detail |
|-----------|--------|
| Cost transparency | Users see what they're spending per action |
| Model flexibility | Different actions can consume different credit amounts |
| Top-up revenue | Users can buy more credits mid-cycle |

| Disadvantage | Detail |
|--------------|--------|
| Cognitive overhead | Users must learn what costs how many credits |
| Depletion anxiety | Running out of credits mid-task is frustrating |

The trend is toward abstracting raw API costs behind higher-level units (requests, tasks) rather than exposing tokens directly. Users pay per "request" or "action" rather than per token.

---

## The "AI Wrapper" Margin Question

RepoQL is **not** a typical AI wrapper — the cloud cache converts per-user embedding costs into shared costs, and the core product (structural queries, local search) runs free without any API calls. But the cloud features do have API-cost exposure.

| Metric | AI Wrappers (typical) | RepoQL (estimated) |
|--------|----------------------|-------------------|
| Gross margin | 25-60% | 57-91% |
| Cost scaling | Linear with users | Sub-linear (cache sharing) |
| Upstream price risk | High (model pricing volatile) | Moderate (Voyage stable, xAI is one provider) |

RepoQL's margin advantage comes from:
1. **Cache amortization** — embedding costs shared across all customers on the same repo
2. **Cheap upstream APIs** — Grok 4.1 Fast and Voyage rerank-2 are among the cheapest in their categories
3. **Optional cloud** — the free local product carries zero API cost

The 3-5x markup rule of thumb for AI wrappers: RepoQL's $10/mo Pro plan charges ~3.8x worst-case costs, ~9.3x typical costs. This is healthy.

> [Market Clarity AI wrapper margins](https://mktclarity.com/blogs/news/margins-ai-wrapper) — 25-60% typical, target 3-5x markup

---

## Stripe and GitHub Marketplace Implementation

### What Stripe Supports

| Model | Stripe Mechanism | Complexity |
|-------|-----------------|------------|
| Per-seat | `quantity` on subscription item. Update on seat change. Auto-proration | Low |
| Usage-based | Stripe Meters + `MeterEvent` API. Aggregation: sum, count, or last | Medium |
| Hybrid (base + usage) | Multiple `items[]` on one subscription (fixed + metered) | Medium |
| Credits | Stripe Meters with pre-paid balance, or manual tracking | Medium |

WorkOS Stripe Seat Sync uses Stripe Meters with `last` aggregation — reports current seat count automatically.

WorkOS Stripe Entitlements sync Stripe subscription features into JWT `entitlements` claim — feature gating without API calls.

> [Stripe seat-based pricing](https://docs.stripe.com/subscriptions/pricing-models/per-seat-pricing) — quantity model
> [Stripe Meters](https://docs.stripe.com/billing/subscriptions/usage-based/meters/configure) — usage reporting
> [Stripe hybrid pricing](https://stripe.com/resources/more/hybrid-pricing-models) — multiple items per subscription
> [WorkOS Stripe add-on](https://workos.com/docs/authkit/add-ons/stripe) — Seat Sync + Entitlements

### What GitHub Marketplace Supports

| Supported | Not Supported |
|-----------|---------------|
| Free plans | Usage-based metering |
| Flat-rate (monthly/yearly) | Tiered pricing within a plan |
| Per-unit (seat-based) | Hybrid (base + usage) |
| 14-day free trials | Non-USD currencies |
| Up to 10 plans | Billing thresholds/alerts |

GitHub Marketplace cannot do usage-based billing. This constrains the billing model if Marketplace is a priority channel.

> [GitHub Marketplace pricing plans](https://docs.github.com/en/apps/github-marketplace/selling-your-app-on-github-marketplace/pricing-plans-for-github-marketplace-apps) — supported models

### Implication for Dual-Channel Billing

If RepoQL wants to offer both Marketplace and Stripe:
- **Marketplace** must be flat-rate or per-seat (no usage component)
- **Stripe** can be hybrid (base + usage)

Options:
1. **Same model on both:** Per-seat with included allowances. Simple but loses usage upside on Stripe
2. **Different models:** Marketplace = per-seat flat-rate. Stripe = hybrid (seat + usage overage). More complex to maintain but captures expansion revenue on Stripe
3. **Marketplace for entry, Stripe for growth:** Use Marketplace as the low-friction entry point. Migrate power users to Stripe for usage-based features. Risk: migration friction

---

## Free Tier Considerations

The existing design has no free cloud tier. Research suggests this may be worth reconsidering.

| Factor | Evidence |
|--------|----------|
| Growth advantage | Companies with well-designed free tiers grow 2-3x faster |
| Developer expectation | Free for open-source is "nearly table stakes" for dev tools |
| Competitive pressure | GitHub Copilot, Amazon Q, Tabnine, Windsurf all offer free tiers |
| Community backlash | Removing free tiers triggers strong negative reactions in developer communities |
| Counter-argument | Cloud features cost real money per call. A free tier must be bounded to avoid unsustainable economics |

> [Monetizely free tier research](https://www.getmonetizely.com/articles/whats-the-optimal-free-tier-limit-for-developer-focused-saas-products) — 15-30% of full capacity
> [Heavybit developer tool pricing](https://www.heavybit.com/library/article/pricing-developer-tools) — free for OSS is table stakes

A bounded free tier (e.g., 50 explain calls/month, unlimited repos with cloud embedding) could serve as a tryout mechanism without creating a sustainability problem. The cloud cache actually helps here — free users on popular repos still benefit from cached embeddings that paid users already generated. Per-repo limits should be avoided entirely — they create friction at exactly the moment users should be exploring freely, and the cache makes the marginal cost near-zero for popular repos.

---

## Gaps

- **Copilot's $0.04/overage request economics:** What's the actual API cost behind a "premium request"? Is $0.04 profitable or subsidized for growth?
- **Cursor's actual margins:** Reports suggest negative margins; Cursor may be operating at a loss subsidized by $3.5B in funding. Not a sustainable model to emulate
- **WorkOS Stripe Entitlements latency:** Entitlements appear in JWTs at next session refresh, not instantly. How long is this delay in practice?
- **GitHub Marketplace metering API for first-party apps:** GitHub uses a metering API for its own Copilot billing. Whether this is available to third-party Marketplace apps is unclear
- **Volume discounts from Voyage/xAI:** At what spend level do API providers offer discounts? Typically 10-30% at $50K+/month
- **Churn patterns by billing model:** Per-seat vs usage-based vs hybrid churn rates not found with sufficient granularity

---

## Summary

The industry has converged on **hybrid (seat + usage)** as the dominant model for AI-powered developer tools. Every major competitor moved toward this pattern in 2025. RepoQL's cost structure — cheap reranking, cheap explain, one-time cached embedding — supports healthy margins under any model.

The existing plan structure (Pro $10/mo flat, Team $25/seat/mo flat) is sustainable but leaves expansion revenue on the table. A hybrid model with included allowances and usage overage would match industry norms and capture value from power users without penalizing light users.

GitHub Marketplace's inability to support usage-based billing creates a channel constraint. The simplest resolution: flat-rate or per-seat on Marketplace, hybrid on Stripe.

The GitHub Marketplace fee is 5% (not 25% as stated in the existing design). This means Marketplace and Stripe pricing can be closer together — the current $12 Marketplace vs $10 Stripe differential is larger than the fee difference justifies.

A bounded free cloud tier may be worth considering. The cloud cache means free-tier users on popular repos cost nearly nothing (cache hits), while providing a tryout path that drives conversion.
