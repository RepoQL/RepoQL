# PHP Format: What Great Looks Like

> An agent should know what a PHP file declares, what namespace it belongs to, and how it fits into the application's class hierarchy — without reading it.

An agent exploring a repository encounters 3,000 PHP files across a Laravel application with controllers, models, services, middleware, jobs, events, and Blade templates. It scans 3,000 headlines and knows what each file is: an Eloquent model that extends Model and uses SoftDeletes, a controller whose public methods are `index`, `store`, `show`, `update`, `destroy`, an abstract service implementing PaymentGateway, a backed string enum defining order statuses, a trait providing audit logging. It filters to the 80 files related to payment processing, reads their structures — method signatures with visibility and types, property declarations with type hints, interface contracts — and understands the payment flow end to end. It queries the graph: "what implements PaymentGateway?" and finds three concrete classes. It asks "what uses the Auditable trait?" and finds every model that gained audit logging through composition. Every file declared a namespace, used imports, mixed traits, implemented interfaces. The agent saw one type hierarchy.

---

## Discovery

- An agent should be able to distinguish file roles from a headline alone — controller, model, service, repository, middleware, job, event, listener, command, migration, test, trait, interface, enum
- An agent should be able to see what a file declares and its namespace without opening it
- An agent should be able to tell a class from an interface from a trait from an enum from a standalone function file from the headline
- An agent should be able to see key method or function names in the headline — enough to judge relevance, not just count

```
headline  →  "PaymentService.php | code.php | 280 ln, ~1.6k tok | ns:App\Services\Payment | class PaymentService implements PaymentGateway | charge, refund, supportedMethods"
headline  →  "Order.php | code.php | 420 ln, ~2.4k tok | ns:App\Models | class Order extends Model | uses: SoftDeletes, HasFactory"
headline  →  "OrderStatus.php | code.php | 35 ln, ~0.2k tok | ns:App\Enums | enum OrderStatus: string | Pending, Processing, Shipped, Delivered, Cancelled"
headline  →  "helpers.php | code.php | 90 ln, ~0.5k tok | ns:(global) | formatCurrency, slugify, parseDate, ..."
```

---

## Structure

- An agent should be able to see every method with its full signature — visibility, static, abstract, parameters with types, return type
- An agent should be able to see every property with its type, visibility, and modifiers (readonly, static)
- An agent should be able to see every constant with its visibility
- An agent should be able to see constructor parameters that are promoted to properties
- An agent should be able to navigate to any declaration by symbol name without reading the whole file

```
structure →
  PaymentService.php (code.php)
    namespace App\Services\Payment
    use App\Contracts\PaymentGateway
    use App\Models\Order
    use App\Exceptions\PaymentFailedException
    use Illuminate\Support\Facades\Log

    + class PaymentService implements PaymentGateway
      +__construct(private readonly HttpClient $client, private readonly Config $config)
      +PaymentResult charge(Order $order, PaymentMethod $method)        #symbol=charge
      +RefundResult refund(string $transactionId, int $amount)          #symbol=refund
      +static array supportedMethods()                                  #symbol=supportedMethods
      #array buildRequest(Order $order)                                 #symbol=buildRequest
      -void validateAmount(int $amount)                                 #symbol=validateAmount
```

---

## Namespace Graph

- An agent should be able to see a file's namespace and all its use-imports
- An agent should be able to find all classes in a given namespace
- An agent should be able to trace use-import chains — what a file depends on
- An agent should be able to find all consumers of a class — everything that use-imports it
- An agent should be able to distinguish between class imports, function imports, and constant imports

```sql
-- What files import PaymentGateway?
SELECT source.uri
FROM edge e
JOIN node source ON source.id = e.source_node_id
WHERE e.type = 'IMPORTS' AND e.properties->>'target' LIKE '%PaymentGateway'
```

---

## Type Hierarchy

- An agent should be able to find all classes that extend a given base class
- An agent should be able to find all classes that implement a given interface
- An agent should be able to find all classes and traits that use a given trait
- An agent should be able to traverse the full inheritance chain — class extends class extends class
- An agent should be able to see the complete type hierarchy of any class: what it extends, what it implements, what traits it uses

Traits are PHP's composition mechanism. They aren't interfaces (no contract) and aren't inheritance (no hierarchy) — they're horizontal code reuse. An agent that can query "what uses this trait?" understands a dimension of structure that extends/implements alone would miss.

```sql
-- Full type hierarchy for a class
SELECT e.type, e.properties->>'target' AS target
FROM node n
JOIN edge e ON e.source_node_id = n.id
WHERE n.kind = 'php.class'
  AND n.properties->>'name' = 'PaymentService'
  AND e.type IN ('EXTENDS', 'IMPLEMENTS', 'USES_TRAIT')
```

---

## Visibility and Access Control

- An agent should be able to filter members by visibility — find all public methods, all protected properties, all private constants
- An agent should be able to see which methods are abstract (must be implemented by subclasses)
- An agent should be able to see which methods are final (cannot be overridden)
- An agent should be able to see which classes are abstract or final
- An agent should be able to see which properties are readonly
- An agent should be able to find a class's public API — its public methods and properties — without seeing internals

```sql
-- Public API of a class
SELECT member.headline
FROM node member
JOIN edge e ON e.destination_node_id = member.id AND e.type = 'HAS_PART'
JOIN node parent ON parent.id = e.source_node_id
WHERE parent.properties->>'name' = 'PaymentService'
  AND member.properties->>'accessibility' = 'public'
```

---

## Enums

- An agent should be able to find all enums and see their cases from structure alone
- An agent should be able to see whether an enum is backed (string or int) and what its backing type is
- An agent should be able to see which interfaces an enum implements
- An agent should be able to see methods defined on an enum
- An agent should be able to find all backed enums whose cases carry specific values

PHP 8.1 enums are first-class types — they implement interfaces, contain methods, and backed enums map to database values. An enum with 5 cases and 3 methods is a real type, not a list of constants.

---

## Attributes

- An agent should be able to see PHP 8 attributes on classes, methods, properties, and parameters
- An agent should be able to find all declarations with a given attribute — all `#[Route]` methods, all `#[Deprecated]` classes
- An agent should be able to see attribute arguments
- An agent should be able to find framework-significant attributes — route definitions, validation rules, event listeners, middleware assignments

Attributes are PHP's metadata mechanism. They replace docblock annotations and carry framework semantics: `#[Route('/api/orders', methods: ['GET'])]` defines routing, `#[AsEventListener]` registers a listener. An agent that can query attributes understands the framework wiring without reading configuration files.

---

## Standalone Functions

- An agent should be able to find all standalone functions (not methods) and see their signatures
- An agent should be able to distinguish between namespaced functions and global functions
- An agent should be able to see helper files that contain only functions — no classes, no interfaces
- An agent should be able to find functions by return type or parameter type

PHP codebases commonly have helper files — `helpers.php`, `functions.php` — containing dozens of standalone functions. These files have no class structure but carry essential utility logic.

---

## Framework Patterns

- An agent should be able to recognize Eloquent model patterns from structure — relationships, scopes, accessors, casts — without hardcoding framework knowledge
- An agent should be able to find all route definitions across controllers
- An agent should be able to find all middleware and see what they guard
- An agent should be able to find all artisan commands and see their signatures
- An agent should be able to find all event/listener pairs from structure
- An agent should be able to find all jobs and see what they handle
- An agent should be able to recognize test classes and see which methods are test cases

---

## Integrity

- An agent should be able to find files with parse errors and see what was recoverable
- An agent should be able to trust that mixed PHP/HTML files (Blade templates, `.phtml`) parse correctly
- An agent should be able to trust that modern PHP syntax — enums, named arguments, match expressions, fibers, readonly classes — parses correctly
- An agent should be able to find use-imports that resolve to nothing — missing classes, wrong namespaces
- An agent should be able to find classes that declare they implement an interface but are missing required methods

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Distinguish file roles from headlines | 3,000 files become navigable in one scan |
| See methods with full signatures | Know a class's API without reading it |
| Trace the full type hierarchy | "What implements X?" and "What uses trait Y?" answer in one query |
| Query traits as a composition dimension | Understand horizontal code reuse that inheritance alone misses |
| Find declarations by attribute | Framework wiring visible without reading config |
| See enums as full types | Cases, methods, interfaces — not just constants |
| Surface visibility on everything | Find the public API of any class in one query |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Read a file to learn its class hierarchy | An agent should see extends, implements, and uses from structure |
| Treat traits as invisible | An agent should query trait usage as a first-class relationship |
| Model PHP attributes as free text | An agent should query attributes by name and arguments |
| Truncate method signatures | An agent should see full parameter types and return types |
| Treat enums as constant lists | An agent should see enum methods and interface implementations |
| Hardcode Laravel/Symfony detection | An agent should recognize framework patterns from structure |
| Ignore visibility modifiers | An agent should filter by public/protected/private |

---

*An agent should be able to understand a PHP codebase as a hierarchy of namespaces, types, and traits — navigable from headline to signature to source — without reading a single file to discover what it declares.*
