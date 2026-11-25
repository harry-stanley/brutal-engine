## Chunk16K: 1000 frames each (after 20 warm-up frames)
| op | median ms | p99 ms | thread bytes | process bytes |
|---|---|---|---|---|
| iter2 spans | 0.766 | 1.435 | 0 | 0 |
| iter2 ForEach | 0.764 | 1.502 | 0 | 0 |
| iter2 ref-pair | 0.986 | 1.947 | 0 | 0 |
| iter2 unchecked | 0.747 | 1.436 | 0 | 0 |
| iter2 SIMD | 0.242 | 0.685 | 0 | 0 |
| iter1 | 0.387 | 1.202 | 0 | 0 |
| iter3 | 1.665 | 2.900 | 0 | 0 |
| parallel8 iter2 | 0.255 | 0.387 | 0 | 0 |
| CB spawn+despawn 100k | 4.750 | 6.439 | 0 | 0 |
| CB add+remove 100k | 6.749 | 9.367 | 0 | 0 |
## Chunk64K: 1000 frames each (after 20 warm-up frames)
| op | median ms | p99 ms | thread bytes | process bytes |
|---|---|---|---|---|
| iter2 spans | 0.585 | 1.154 | 0 | 0 |
| iter2 ForEach | 0.543 | 1.155 | 0 | 0 |
| iter2 ref-pair | 0.691 | 1.273 | 0 | 0 |
| iter2 unchecked | 0.638 | 1.131 | 0 | 0 |
| iter2 SIMD | 0.170 | 0.254 | 0 | 0 |
| iter1 | 0.240 | 0.531 | 0 | 0 |
| iter3 | 1.614 | 2.946 | 0 | 0 |
| parallel8 iter2 | 0.203 | 0.282 | 0 | 0 |
| CB spawn+despawn 100k | 4.817 | 6.194 | 0 | 0 |
| CB add+remove 100k | 6.794 | 9.845 | 0 | 0 |
## Table: 1000 frames each (after 20 warm-up frames)
| op | median ms | p99 ms | thread bytes | process bytes |
|---|---|---|---|---|
| iter2 spans | 0.557 | 0.999 | 0 | 0 |
| iter2 ForEach | 0.738 | 1.155 | 0 | 0 |
| iter2 ref-pair | 0.696 | 1.354 | 0 | 0 |
| iter2 unchecked | 0.658 | 1.143 | 0 | 0 |
| iter2 SIMD | 0.131 | 0.173 | 0 | 0 |
| iter1 | 0.222 | 0.472 | 0 | 0 |
| iter3 | 1.526 | 2.435 | 0 | 0 |
| parallel8 iter2 | 0.187 | 0.238 | 0 | 0 |
| CB spawn+despawn 100k | 4.573 | 5.677 | 0 | 0 |
| CB add+remove 100k | 6.562 | 9.355 | 0 | 0 |
