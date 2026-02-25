package concurrent

func Run(input <-chan int) chan<- int {
    out := make(chan int)
    go func() {
        for value := range input {
            out <- value
        }
    }()

    select {
    case out <- 1:
    default:
    }

    return out
}

var global chan string

