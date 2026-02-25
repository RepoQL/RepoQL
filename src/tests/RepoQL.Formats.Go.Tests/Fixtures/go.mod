module github.com/acme/service

go 1.22
toolchain go1.22.1

require (
    github.com/gin-gonic/gin v1.10.0
    github.com/stretchr/testify v1.9.0 // indirect
)

require golang.org/x/text v0.16.0

replace github.com/acme/common => ../common
replace example.com/fork v1.2.3 => github.com/acme/fork v1.2.4

retract [v1.0.0, v1.1.0] // security issue
retract v1.2.0
