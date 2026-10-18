# Prom installment receipt matching — 1.0.75

Two Prom orders with identical complete baskets and totals could remain unlinked even when their payments differed. The existing matcher recognized Prom-payment and Nova Poshta COD, but treated the API label `Оплатить частями` as unknown. That unknown order remained a competitor for both the NovaPay and RozetkaPay receipts.

Prom's [seller documentation](https://support.prom.ua/hc/uk/articles/19362823022621) describes its installment payment service and settlement/refunds through RozetkaPay. Recognize the exact Ukrainian and Russian installment labels, with optional `від Rozetka` / `от Rozetka`, only for Prom connections (which also include Bigl orders). Existing whitespace/case normalization applies. Custom labels and installment labels on other marketplaces remain unknown.

Payment still only separates an ambiguous group after its complete baskets match. Amount, Kyiv date, quantity, product contradictions, complete coverage, manual decisions and fiscal IDs retain their existing rules. Receipt payments must cover the full total with one recognized cashless provider. No nearest-time selection or provider inference from a substring was added.

The graph input fingerprint now includes the payment rules version. A cached unresolved graph from 1.0.74 must be recalculated without deleting cached receipt details, stored automatic pairs, exact links or manual decisions. Keeping the graph schema also preserves the existing legacy exact-link migration path.

Regression coverage uses synthetic documents with equal amounts and baskets. Before the fix, the installment-pair regression and the genuine old-input-fingerprint regression both failed; unsupported-label guards passed. The tests cover reversed API order, Ukrainian/Russian/case/space variants, unknown custom labels, marketplace scope, missing baskets, incomplete coverage, cache invalidation and saved-pair restoration after restart.

No real API calls or printer jobs are needed to diagnose or test this matching change.

Validation on 2026-10-19: Development Release passed **732/732** tests, including all 12 payment regression groups. A read-only run of the updated matcher against the existing local snapshot (1,354 orders and 179 receipts) resolved both reported pairs with `AmountAndPayment`; no profile save, API request or printer job was performed.
