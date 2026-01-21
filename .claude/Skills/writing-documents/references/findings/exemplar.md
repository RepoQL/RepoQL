# SMS Delivery Pathways

**Question**: How does the system send SMS messages, and what determines which delivery method is used?

---

**SMS Routing**: Two pathways based on carrier. AT&T routes through Bandwidth API with delivery receipts. All other carriers use email-to-SMS gateways with no delivery confirmation. The carrier lookup happens at send time—there's no fallback between methods.

---

## Routing Detail

```mermaid
flowchart LR
    Send[Send Request] --> Lookup[Carrier Lookup]
    Lookup --> Check{AT&T?}
    Check -->|Yes| BW[Bandwidth API]
    Check -->|No| Gateway[Email Gateway]
    BW --> Receipt[Delivery Receipt]
    Gateway --> Sent[Sent - No Confirmation]
```

| Path | Carriers | Delivery Confirmation | Rate Limit |
|------|----------|----------------------|------------|
| Bandwidth API | AT&T only | Yes, via webhook | 100/min |
| Email gateway | All others | No | 500/min |

The `send_type` field determines the path—exclusive routing, not fallback. A message never tries both.

> [SmsService.php:36-80](https://github.com/example/repo/blob/main/src/SmsService.php#L36-L80) — carrier detection and routing logic

> [SmsQueueProcessor.php:142](https://github.com/example/repo/blob/main/src/SmsQueueProcessor.php#L142) — rate limiting per pathway

## Delivery Confirmation

Bandwidth provides delivery receipts via webhook. The system updates message status within 30 seconds of carrier confirmation.

Email gateways provide no confirmation. Messages show "Sent" immediately but actual delivery is unknown. Failed deliveries surface only if the gateway returns a bounce—which most don't.

> [WebhookController.php:88-120](https://github.com/example/repo/blob/main/src/WebhookController.php#L88-L120) — receipt processing

## Practical Effects

| Action | Effect |
|--------|--------|
| Send to AT&T number | Delivery tracked, status updated |
| Send to other carrier | Status stays "Sent" regardless of actual delivery |
| Gateway bounce received | Status updates to "Failed" (rare) |
| Carrier lookup fails | Message queued, retried with exponential backoff |
