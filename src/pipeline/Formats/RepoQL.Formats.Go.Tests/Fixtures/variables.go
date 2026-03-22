package vars

import (
    "errors"
    "fmt"
)

type Runner interface {
    Run()
}

type Server struct{}

func (s *Server) Run() {}

type CustomError struct{}

func (e *CustomError) Error() string {
    return "custom"
}

var (
    ErrClosed  = errors.New("closed")
    ErrWrapped = fmt.Errorf("wrapped: %w", ErrClosed)
    ErrCustom  = &CustomError{}
    _          Runner = (*Server)(nil)
    _          Runner = Server{}
    Count      int    = 42
    plain              = "value"
)

