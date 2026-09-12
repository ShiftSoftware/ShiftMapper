# ShiftMapper documentation

[`README.md`](../README.md) in the repository root is the front door — what ShiftMapper is, how to
install it, and a five-minute tour. These pages are the deeper reference behind it.

| Page | Read it when |
|---|---|
| [Getting started](getting-started.md) | You have installed the package and want a working mapper |
| [Conversions](conversions.md) | You need to know whether *this* type becomes *that* one, and what happens at the edges |
| [Diagnostics](diagnostics.md) | A build said `SM00NN` and you want the answer in ten seconds |
| [Extension points](extension-points.md) | You are shipping a **library** whose maps must apply in every application that references it |
| [Migrating from AutoMapper](automapper-migration.md) | You have an AutoMapper configuration to port, and want the honest gaps as well as the table |

## The two things worth knowing before anything else

**Every feature answers for both backends, or says which one it does not support.** `Map` works on
objects you already have; `ProjectTo` turns a map into part of a database query. A feature that
cannot do both is not quietly half-supported — the build tells you, by id, at the line responsible.
That is what most of the diagnostics are for.

**Configuration the generator cannot bake is an error, never a silent default.** Everything is read
from your source at compile time, so a declaration the generator cannot honour where it is written
stops the build (`SM0035`) rather than being applied in some way you did not ask for. A mapper that
does not do what its source says, and says nothing about it, is the one outcome this library
refuses.

## The working example

[`ShiftMapper.Sample`](../ShiftMapper.Sample) is a runnable ASP.NET application that demonstrates
every feature in the library, with the reasoning in comments beside each one and a
[`.http` file](../ShiftMapper.Sample/ShiftMapper.Sample.http) you can step through request by
request. Where these pages cite an endpoint — `GET /api/products/list`, say — it is a real endpoint
you can call.

`GET /` on the running sample lists every route with a one-line description of what it demonstrates.

---

### A note on the shape of the diagnostics reference

The roadmap for this step asked for **one page per `SM` id**, on the reasoning that an id is what
people search for. It is one page with a section and a stable anchor per id instead —
[`diagnostics.md#sm0011`](diagnostics.md) — for two reasons worth writing down rather than leaving
as a silent deviation:

- **Completeness is checkable.** There are 38 rules and the set grows. One file can be diffed
  against `DiagnosticDescriptors.All` in a single pass; thirty-eight files drift, and a missing page
  looks exactly like a rule that was never added.
- **Nothing is published yet.** Per-id pages buy search placement, which is worth having when there
  is search traffic to place. Splitting the file later is mechanical; reconciling thirty-eight
  drifted pages is not.
