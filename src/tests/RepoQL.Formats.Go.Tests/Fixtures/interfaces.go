package handlers

type Handler interface {
    Handle(ctx Context, req Request) Response
    Validate(req Request) error
}

type Middleware interface {
    Handler
    Before(ctx Context) error
    After(ctx Context, resp Response) Response
}

type Closer interface {
    Close() error
}

