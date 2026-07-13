# User Stories

## Storefront / Customer
- As a customer, I can place an order containing one or more line items so that I can purchase products.
- As a customer, I can view the real-time status of my order (placed → inventory reserved → payment charged → fulfillment triggered → shipped) so that I know where it stands.
- As a customer, if my order fails at any stage (payment decline, inventory shortfall), I receive a clear reason and my order is not left in a partial or ambiguous state.
- As a customer, I cannot be double-charged if my payment webhook is delivered more than once.

## Inventory
- As the system, when an order is placed, inventory is reserved for each line item before payment is attempted, so that we never charge for stock we don't have.
- As the system, if two customers attempt to buy the last unit of a product simultaneously, exactly one reservation succeeds and the other fails cleanly, so that we never oversell.
- As an ops user, I can view current on-hand, reserved, and available inventory per SKU.
- As the system, if a downstream step fails after inventory is reserved, the reservation is released automatically (compensation), so that stock isn't locked indefinitely.

## Payment
- As the system, once inventory is reserved, payment is charged against the order total, so that fulfillment only proceeds for paid orders.
- As the system, a duplicate payment provider callback for the same order does not result in a duplicate charge or duplicate state transition (idempotency).
- As the system, if payment fails, any inventory reservation for that order is released and the order is marked failed with a customer-facing reason.
- As an ops user, I can view payment attempt history per order, including retries.

## Fulfillment / Shipping
- As the system, once payment succeeds, a fulfillment request is sent to the (simulated) warehouse/carrier so that the order can ship.
- As the system, if the fulfillment/carrier call fails transiently, it is retried with backoff before being treated as a hard failure.
- As an ops user, I can see which orders are stuck in a retry or dead-letter state and why.

## Notifications
- As a customer, I receive a notification at each major order state transition (confirmed, payment failed, shipped).
- As the system, notification failures never block or roll back the underlying order state — notification is best-effort, not transactional.

## Platform / Ops / Architecture Proof Points
- As an ops user, I can view a live distributed trace for any single order across all services (Aspire dashboard / OpenTelemetry).
- As an architect reviewing this system, I can find an ADR explaining why each major structural decision was made (saga vs. 2PC, Cosmos vs. SQL for the order event log, what was deliberately *not* decomposed into its own service).
- As a demo operator, I can deliberately kill a dependency (payment gateway, Service Bus) mid-flow and observe the system detect, retry, dead-letter, and/or compensate correctly — on camera, live.
- As a developer, integration tests run against real emulated infrastructure (Aspire-orchestrated containers), not all-mocked unit tests, so that the tests prove something.