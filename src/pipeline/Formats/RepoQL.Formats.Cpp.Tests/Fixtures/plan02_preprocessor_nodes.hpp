#include "pool.h"
#include <vector>

#define TRACE_CALL(x) x

class ResolvedTarget {};
using ResolvedAlias = ResolvedTarget;
using ::ResolvedTarget;

#ifdef FEATURE_FLAG
class FeatureGate {};
#endif
