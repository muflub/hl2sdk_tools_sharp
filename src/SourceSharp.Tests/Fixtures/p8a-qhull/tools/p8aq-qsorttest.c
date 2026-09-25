/* p8aq-qsorttest.c - check that this glibc's qsort() on an array of pointers
 * behaves exactly like a top-down merge sort (n1 = n/2, take-left when
 * cmp <= 0), including with qhull's inconsistent qh_compareangle comparator
 * (which never returns 0).  The C# port implements that merge sort. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
typedef struct { double angle; int type; int tag; } M;
static int cmpangle(const void *p1, const void *p2) {
  M *a = *(M **)p1, *b = *(M **)p2;
  return ((a->angle > b->angle) ? 1 : -1);
}
static int cmptype(const void *p1, const void *p2) {
  M *a = *(M **)p1, *b = *(M **)p2;
  return a->type - b->type;
}
static void msort(M **b, size_t n, M **tmp, int (*cmp)(const void *, const void *)) {
  size_t n1, n2, i = 0;
  M **b1, **b2;
  if (n <= 1) return;
  n1 = n / 2; n2 = n - n1;
  b1 = b; b2 = b + n1;
  msort(b1, n1, tmp, cmp);
  msort(b2, n2, tmp, cmp);
  while (n1 > 0 && n2 > 0) {
    if (cmp(b1, b2) <= 0) { tmp[i++] = *b1++; n1--; }
    else { tmp[i++] = *b2++; n2--; }
  }
  while (n1 > 0) { tmp[i++] = *b1++; n1--; }
  memcpy(b, tmp, i * sizeof(M *));
}
int main(void) {
  int trial, n, i, bad = 0, total = 0;
  static M pool[3000];
  static M *a[3000], *c[3000], *tmp[3000];
  srand(12345);
  for (trial = 0; trial < 20000; trial++) {
    n = 1 + rand() % (trial < 10000 ? 40 : 2500);
    for (i = 0; i < n; i++) {
      pool[i].angle = (double)(rand() % 7) * 0.25 - 0.5;
      if (rand() % 13 == 0) pool[i].angle = 0.0 / 0.0; /* NaN */
      pool[i].type = rand() % 5;
      pool[i].tag = i;
      a[i] = c[i] = &pool[i];
    }
    if (trial & 1) {
      qsort(a, n, sizeof(M *), cmpangle);
      msort(c, n, tmp, cmpangle);
    } else {
      qsort(a, n, sizeof(M *), cmptype);
      msort(c, n, tmp, cmptype);
    }
    total++;
    if (memcmp(a, c, n * sizeof(M *))) bad++;
  }
  printf("qsort-vs-msort trials %d mismatches %d\n", total, bad);
  return bad != 0;
}
