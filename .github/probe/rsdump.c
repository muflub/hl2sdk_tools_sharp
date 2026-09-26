// Characterises this CPU's rsqrtss: which input bits the estimate depends on,
// and the full table over [1,4) (both exponent parities), in a compact form.
#include <xmmintrin.h>
#include <stdio.h>
#include <string.h>
#include <stdint.h>
static uint32_t rs(uint32_t in){ float f; memcpy(&f,&in,4); float r=_mm_cvtss_f32(_mm_rsqrt_ss(_mm_set_ss(f))); uint32_t u; memcpy(&u,&r,4); return u; }
int main(void){
  // inputs 1.0 (0x3F800000) .. just below 4.0 (0x40800000): exponent 127 and 128
  uint32_t lo=0x3F800000, hi=0x40800000;
  int k;
  for(k=1;k<=23;k++){ // block size 2^(23-k) within one mantissa
    uint32_t bs=1u<<(23-k); int ok=1;
    for(uint32_t x=lo;x<hi && ok;x+=bs){ uint32_t r0=rs(x); for(uint32_t y=x+1;y<x+bs;y++) if(rs(y)!=r0){ok=0;break;} }
    if(ok) break;
  }
  printf("K %d\n",k);
  // exponent separability: rs(x*4) == rs(x) with exponent-1, over many exponents
  long bad=0;
  for(uint32_t x=lo;x<hi;x+=97){ uint32_t r=rs(x); for(int d=-20;d<=20;d++){ uint32_t xx=x+((uint32_t)(2*d)<<23); uint32_t want=r-((uint32_t)d<<23); if(rs(xx)!=want) bad++; } }
  printf("SEPARABLE_BAD %ld\n",bad);
  uint32_t bs=1u<<(23-k);
  printf("TABLE");
  int n=0; for(uint32_t x=lo;x<hi;x+=bs){ if(n%16==0) printf("\nT"); printf(" %08x",rs(x)); n++; }
  printf("\nN %d\n",n);
  return 0;
}
