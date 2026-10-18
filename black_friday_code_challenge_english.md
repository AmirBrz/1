# Black Friday Sale Engine with Multi-Tenant Architecture

## Background

You are joining the Platform Engineering team at a company that builds white-label e-commerce infrastructure for large retailers. Each retailer operates on the platform as an independent **tenant**, with its own product catalog, pricing rules, and customers.

Black Friday is the most critical commercial event of the year for our customers. Last year, two of our largest customers experienced system failures at exactly the same time—9:00 AM, when major sales began. During the first four minutes of the sale, the platform oversold high-demand SKUs. This led to canceled orders, widespread customer complaints, and the activation of financial penalty clauses in our contracts.

Your task is to design and implement the core backend service that prevents this incident from happening again.

---

## The Business Problem

During Black Friday:

- A tenant activates a time-limited promotion for one or more products (SKUs). Each product has a specific discounted price and a maximum sale quantity. For example, 300 65-inch televisions at a 35% discount from 9:00 AM to 9:30 AM.
- Exactly when the sale starts, demand may surge to **thousands of concurrent reservation requests per minute** for a particular product within one tenant.
- The platform **must never sell more units than the declared quantity**. Overselling is a direct contractual violation and can result in significant financial penalties.
- Completing a purchase involves several downstream operations: deducting inventory from the warehouse, creating an order record, and sending a confirmation notification. These operations take time. The response to a customer's reservation request must be immediate, while order processing and fulfillment may happen **out of band**, provided that the process is reliable and guaranteed.
- If the order-processing pipeline is temporarily unavailable or a job fails, the task must not disappear without a trace. Retry support and clear visibility into error logs are mandatory.
- Dozens of tenants may run Black Friday sales at the same time. Their data, processing pipelines, and error states must be **fully isolated**. A traffic spike or outage affecting one tenant must not negatively affect another.

---

## Expected Deliverable: What You Must Build

Build a **Black Friday Sale Reservation Service** with the following capabilities.

### 1. Sale Event Management

- Create a sale event for a tenant, including the SKU, inventory quantity, discounted price, start timestamp, and end timestamp.
- Retrieve the current state of a sale event, including remaining inventory, whether the event is active or inactive, and the associated tenant context.

### 2. Reservation Flow

- Accept customer reservation requests while a sale event is active.
- Check and decrement inventory **atomically**. No race conditions are acceptable; concurrent requests must never cause the declared inventory limit to be exceeded.
- After a successful reservation, submit a processing job to a background pipeline. This pipeline is responsible for creating the order, synchronizing warehouse inventory, and sending a notification. The business logic inside each step may be stubbed or mocked, but the pipeline infrastructure itself must be real, running, and observable.
- If the pipeline becomes unavailable or a job fails after being accepted, the system must not silently discard it. Implement at least one retry mechanism and a dead-letter queue (DLQ) for jobs that reach the retry limit.

### 3. Read Performance Under Burst Traffic

- Sale status information (real-time inventory for display to users, sale metadata, and tenant configuration) is read much more frequently than it is written, especially during the first seconds after a sale opens.
- Reading this information directly from the primary database for every request is not acceptable under Black Friday traffic. Implement a read strategy that keeps this data available with low latency while preserving the level of consistency required by the business.
- Clearly document the trade-offs you make regarding consistency and staleness.

### 4. Tenant Isolation

- All storage areas, processing pipelines, and intermediate data layers must be namespaced or partitioned by tenant. A misconfiguration or bug in one tenant's sale must not affect another tenant's inventory counters, job queues, or error states.

---

## Technical Constraints

- **Language/framework:** .NET 8 or later (C#). You may use any libraries you consider appropriate.
- **Database:** SQL Server. Include it in Docker Compose; the `mcr.microsoft.com/mssql/server` image is suitable.
- **Deployment:** The entire system must start with only the `docker-compose up` command, with no additional manual steps other than those documented in the README.
- **API:** RESTful HTTP. Swagger/OpenAPI documentation is a plus, but not mandatory.
- **Testing:** Include at least one meaningful test (unit test, integration test, or concurrency script) demonstrating that reservation logic remains correct under concurrent load. A simple Bash script using `curl` is acceptable if it clearly demonstrates correct system behavior.

You are free to add any other infrastructure components needed to meet these requirements. However, every additional component must be justified in an ADR (Architecture Decision Record).

---

## Seed Data

Running `docker-compose up` must start a system that can be tested and inspected immediately, without manually modifying the database. Add a seeding step—using migrations, an EF Core data seeder, a SQL initialization script, or a similar startup mechanism—that creates:

- At least two tenants (for example, `tenant-electronics` and `tenant-fashion`).
- At least three sale events per tenant, with different states:
  - One active event (started in the past and ending in the future).
  - One upcoming event (starting in the future).
  - One expired event (ended in the past).
- At least ten different products (SKUs) across these tenants, with realistic names and prices.
- Inventory quantities suitable for easy concurrency testing. Values between 50 and 200 units are recommended for quick load tests.

Seed data must be deterministic and repeatable (idempotent), so the system can be shut down and started again cleanly.

---

## Deliverables

Submit a Git repository (on GitHub, GitLab, or as a ZIP file) containing:

1. **Runnable source code** that starts cleanly and reliably with `docker-compose up`.
2. **A README** containing:
   - Instructions for starting the system and testing the API. Example `curl` commands or a Postman collection are a plus.
   - A high-level overview of the system architecture. An architecture diagram is welcome but optional.
   - Assumptions made where the requirements are ambiguous.
   - Known limitations and what you would improve if you had more time.
3. **At least one ADR** for every significant infrastructure choice beyond the required base stack. Each ADR must describe the context, alternatives considered, final decision, and consequences.
4. **Evidence that the system works correctly under concurrent load**: logs, test files, scripts, or output demonstrating that reservation logic does not oversell under concurrent requests.

---

## Evaluation Criteria

The project will be evaluated across four dimensions:

- **Correctness:** Does the system actually prevent overselling under concurrent load?
- **Architecture:** Are infrastructure choices reasoned and justified, rather than added without careful consideration?
- **Communication:** Are the README and ADR documents clear enough for both technical and non-technical business stakeholders to understand the trade-offs?
- **Craft:** Is the code readable, follows engineering standards, and maintainable by the team?

The use of AI tools is fully allowed and encouraged. We are not trying to test how much boilerplate code you can write from memory.

---

If you have questions about the coding challenge, ask them. We would rather you ask than proceed based on a fundamentally incorrect assumption.

---

# Suggested Prompt for the AI Agent

Compare the requirements in this document with the existing project in the repository.

**Do not start implementing changes yet. First inspect the existing codebase and produce a gap analysis.**

For every requirement:

1. Identify whether it is **already implemented**, **partially implemented**, **missing**, or **cannot be verified from the repository**.
2. Provide evidence: relevant file paths, classes, methods, configuration, tests, and/or infrastructure definitions. Do not mark a requirement as implemented based only on a class or interface name; verify the actual behavior where possible.
3. Explain what needs to change or be added to satisfy the requirement.
4. Distinguish between:
   - Reusing existing functionality.
   - Modifying existing code.
   - Adding new code or infrastructure.
   - Adding or updating tests and documentation.
5. Identify risks, architectural conflicts, hidden assumptions, and dependencies between proposed changes.
6. Prioritize gaps by importance, with preventing overselling and ensuring reliable background-job processing treated as critical.

Pay particular attention to:

- Atomic inventory reservation under concurrent requests, including whether the implementation is safe across multiple application instances.
- Reliable handoff from reservation to background processing, including failure scenarios, retries, and dead-letter handling.
- Read performance and the consistency/staleness trade-offs of any cache or other read strategy.
- Tenant isolation across database records, caches, queues, background jobs, and error tracking.
- Sale start/end time validation and sale-event state handling.
- Docker Compose startup, SQL Server readiness, database migrations, and deterministic/idempotent seed data.
- Concurrency tests that prove the system never reserves more units than are available.
- README and ADR coverage for significant infrastructure decisions.

## Required Output

Produce a structured report with these sections:

1. **Executive summary** — overall readiness and the most important gaps.
2. **Requirement-by-requirement gap analysis** — include the requirement, status, evidence, gap, and recommended action.
3. **Implementation plan** — prioritized steps, grouped into critical, high, and lower priority.
4. **Files likely to be changed or added** — explain the purpose of each proposed change.
5. **Testing and verification plan** — explain how to verify each critical requirement, especially concurrency safety, background-job reliability, and tenant isolation.
6. **Assumptions and open questions** — list anything that cannot be determined from the codebase and questions that should be answered before implementation.

Be precise and evidence-based. Do not assume a requirement is satisfied merely because a related technology or package is present. Do not make code changes until the gap analysis and implementation plan have been presented.
