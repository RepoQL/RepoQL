package model

type UserID int64
type DisplayName = string
type Labels = map[string]string
type Counter uint32

type Service struct {
    Name string
}

type Runner interface {
    Run()
}

