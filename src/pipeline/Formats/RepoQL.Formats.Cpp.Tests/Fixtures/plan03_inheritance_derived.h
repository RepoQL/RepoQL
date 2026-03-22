#include "plan03_inheritance_base.h"

namespace net {
class TcpTransport : public Transport {};
class VirtualTransport : virtual public Base {};
class MultiTransport : public Transport, private SocketBase {};
}
