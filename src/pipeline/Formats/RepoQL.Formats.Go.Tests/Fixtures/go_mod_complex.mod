module github.com/acme/complex

go 1.23
toolchain go1.23.0

require github.com/pkg/errors v0.9.1
require (
    github.com/sirupsen/logrus v1.9.3
    github.com/google/uuid v1.6.0 // indirect
    malformed-require-line
)

replace old.example/mod => ../local/mod
replace (
    github.com/acme/thing v1.0.0 => github.com/acme/thing v1.0.1
    bad replace line
)

retract [v0.9.0, v0.9.5] // yanked
retract v1.0.0
