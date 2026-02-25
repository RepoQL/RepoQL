package main

import (
    "fmt"
    "net/http"

    "github.com/gorilla/mux"
)

// Server handles HTTP requests.
type Server struct {
    DB     *sql.DB `json:"db" db:"database"`
    Logger Logger
    http.Handler // embedded field
    port   int
}

func (s *Server) Serve(addr string) error {
    return http.ListenAndServe(addr, s)
}

func (s Server) String() string {
    return fmt.Sprintf("Server on port %d", s.port)
}

func NewServer(db *sql.DB) *Server {
    return &Server{DB: db}
}

