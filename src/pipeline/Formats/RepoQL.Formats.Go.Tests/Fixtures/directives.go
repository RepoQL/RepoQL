//go:build linux && amd64
//go:generate go run ./cmd/gen

package directives

import _ "unsafe"

//go:embed assets/*
var assetPattern string

//go:linkname runtimeNano runtime.nanotime
func runtimeNano() int64

