# Service boundaries

## The bad split

`examples/AntiPatterns/WrongServiceBoundaries` shows a Controller Service, a Validation Service, and a Repository Service. Placing one order makes three calls. Every feature that validates and saves pays the same tax. The services change together because they are layers of one use case, not separate businesses.

Technical-layer decomposition creates chatty dependencies:

- The controller cannot finish without the validator.
- The validator cannot be deployed alone because its input is the controller's private shape.
- A change to a field ripples through every layer service.
- Failure of the "repository service" is failure of every business capability that was forced to share it.

## The split used here

| Service | Business capability | Owns |
| --- | --- | --- |
| Order | Checkout workflow and the customer's order | Orders, lines, status, saga history |
| Inventory | Stock the business can promise | Available quantity, reserved quantity, reservations |
| Payment | Simulated authorization | Authorization outcome and idempotency |
| Notification | Telling the customer what happened | Notification records and delivery status |

These are bounded contexts. Order does not calculate stock. Inventory does not decide that an order is confirmed. Payment does not release stock. Notification does not authorize money.

Inside each service the code is a small modular boundary: domain rules, a database, an API, and messaging. That cohesion is the point. The network is only at the edge.

## A bounded context is not automatically a service

A bounded context is a language and a consistency boundary. It can live as a module in one process. Split it into a deployable service when you have a reason that a module cannot give you:

- The capability needs to scale, fail, or be secured differently.
- A team needs to deploy it on its own cadence.
- The data lifecycle is different enough that a shared database is doing harm.

If none of those are true, a modular monolith keeps the same boundaries without the broker, the saga, and the partial-failure work. This repository is a demonstration of the distributed shape. It is not an argument that four processes are always better than four modules.

## Decision guide

Prefer a **modular monolith** when:

- One team owns the whole checkout flow.
- You deploy the system together anyway.
- You have not measured a scaling or isolation problem.
- You want local transactions for the common success path.

Prefer **separate services** when:

- Independent deployability is already a real constraint.
- You accept eventual consistency and operational load.
- The data ownership lines are stable.
- You have health checks, traces, and a way to repair stuck workflows.

Moving a tangled monolith to the network first, and discovering the boundaries later, produces the system in `docs/DistributedMonolith.md`.
