# Class Diagram Reference

**Use for**: Object-oriented design, inheritance hierarchies, interface contracts, API surface design

**Don't use for**: Database schema (use ER diagram), instances, data flow

---

## Basic Syntax

```mermaid
classDiagram
    class IProcessor {
        <<interface>>
        +Process(request)* Result
    }

    class BaseProcessor {
        <<abstract>>
        #Validate(request) bool
        +Process(request) Result
    }

    class ConcreteProcessor {
        -config Config
        +Process(request) Result
    }

    IProcessor <|.. BaseProcessor : implements
    BaseProcessor <|-- ConcreteProcessor : extends

    %% MEANING: Processor inheritance hierarchy
    %% SYMBOLS: + public, # protected, - private, * abstract
```

---

## Visibility Modifiers

| Symbol | Meaning |
|--------|---------|
| `+` | Public |
| `-` | Private |
| `#` | Protected |
| `~` | Package/internal |
| `*` | Abstract |

---

## Stereotypes

```mermaid
classDiagram
    class IService {
        <<interface>>
    }
    class BaseClass {
        <<abstract>>
    }
    class EnumType {
        <<enumeration>>
        VALUE_A
        VALUE_B
    }
    class ServiceImpl {
        <<service>>
    }
```

Common stereotypes: `interface`, `abstract`, `enumeration`, `service`, `record`

---

## Relationships

```mermaid
classDiagram
    A <|-- B : Inheritance
    C *-- D : Composition
    E o-- F : Aggregation
    G --> H : Association
    I ..> J : Dependency
    K <|.. L : Realization
```

| Symbol | Meaning | Use for |
|--------|---------|---------|
| `<\|--` | Inheritance | Class extends class |
| `<\|..` | Realization | Class implements interface |
| `*--` | Composition | Part cannot exist without whole |
| `o--` | Aggregation | Part can exist independently |
| `-->` | Association | Reference/uses |
| `..>` | Dependency | Temporary use |

---

## Cardinality

```mermaid
classDiagram
    Customer "1" --> "*" Order : places
    Order "1" --> "1..*" LineItem : contains
```

| Notation | Meaning |
|----------|---------|
| `1` | Exactly one |
| `0..1` | Zero or one |
| `*` | Zero or more |
| `1..*` | One or more |
| `n` | Specific number |

---

## Methods and Properties

```mermaid
classDiagram
    class Service {
        +string Name
        -int count
        +DoWork() void
        +GetResult(id) Result
        #ValidateInput(data) bool
        -Initialize() void
    }
```

Format: `visibility name(params) returnType`

---

## Generic Types

```mermaid
classDiagram
    class Repository~T~ {
        +Get(id) T
        +Save(entity T) void
        +Query() List~T~
    }
```

Use `~T~` for generic type parameters.

---

## Namespaces

```mermaid
classDiagram
    namespace Services {
        class UserService
        class OrderService
    }

    namespace Repositories {
        class UserRepository
        class OrderRepository
    }

    UserService --> UserRepository
    OrderService --> OrderRepository
```

---

## Best Practices

- Use stereotypes (`<<interface>>`, `<<abstract>>`)
- Show visibility (`+`, `-`, `#`)
- Include key methods with return types
- Keep to 8 classes max per diagram
- Focus on API surface, not implementation details
- Use namespaces for logical grouping

---

## Common Mistakes

- Too much implementation detail (methods not relevant to design)
- Too many classes (>10 becomes unreadable)
- Using for database tables (use ER diagram)
- Missing stereotypes on interfaces/abstracts
- Missing visibility modifiers
- No relationship labels

---

## Class vs ER Diagram

| Aspect | Class Diagram | ER Diagram |
|--------|---------------|------------|
| Focus | Code structure | Data storage |
| Methods | Yes | No |
| Inheritance | Common | Rare |
| Cardinality | Optional | Required |
| Primary use | API design | Schema design |

---

*Class diagrams show WHAT the code structure looks like before implementation.*
