package collections

type Set[T comparable] struct {
	items map[T]struct{}
}

func NewSet[T comparable]() *Set[T] {
	return &Set[T]{items: make(map[T]struct{})}
}

func (s *Set[T]) Add(v T) {
	s.items[v] = struct{}{}
}

func (s *Set[T]) Contains(v T) bool {
	_, ok := s.items[v]
	return ok
}

func (s Set[T]) Len() int {
	return len(s.items)
}

type Pair[K comparable, V any] struct {
	Key   K
	Value V
}

func MakePair[K comparable, V any](k K, v V) Pair[K, V] {
	return Pair[K, V]{Key: k, Value: v}
}

type Ordered interface {
	~int | ~float64 | ~string
}

func Min[T Ordered](a, b T) T {
	if a < b {
		return a
	}
	return b
}
