"""NumPy replica of shader hash / Box-Muller (same integer keys, float32 domain)."""
import numpy as np

def noise(seed,size):
    def hash32(x):
        x=np.asarray(x,dtype=np.uint32)
        x=x^(x>>np.uint32(16));x=x*np.uint32(0x7feb352d)
        x=x^(x>>np.uint32(15));x=x*np.uint32(0x846ca68b)
        return x^(x>>np.uint32(16))
    def uniform(k):return ((hash32(k)&np.uint32(0xffffff)).astype(np.float32)+np.float32(1))/np.float32(16777217)
    yy,xx=np.indices((size,size),dtype=np.uint32)
    key=np.uint32(seed)^(xx*np.uint32(0x9e3779b9))^(yy*np.uint32(0x85ebca6b))
    u=uniform(key);v=uniform(key^np.uint32(0x68bc21eb))
    return (np.sqrt(np.float32(-2)*np.log(u))*np.cos(np.float32(6.28318530718)*v)).astype(np.float32)
