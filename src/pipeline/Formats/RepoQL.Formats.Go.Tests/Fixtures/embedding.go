package models

import "sync"

type Base struct {
    ID   int64
    Name string
}

type User struct {
    Base       // embedded same-package type
    sync.Mutex // embedded qualified type
    Email string
}

