"""Integrate a lit 96-plane density field into optical slabs for the real-time sky shader."""
from pathlib import Path
import numpy as np
from PIL import Image
from scipy.ndimage import map_coordinates

w, h, depth = 384, 144, 96
rng = np.random.default_rng(39017)
grids = [rng.uniform(-1, 1, (37, 41, 59)).astype("f") for _ in range(5)]
z, y, x = np.meshgrid(np.linspace(-1, 1, depth, dtype="f"), np.linspace(-1, 1, h, dtype="f"),
                      np.linspace(-1, 1, w, dtype="f"), indexing="ij")


def noise(x, y, z, scale, index):
    coords = np.array([z*scale*.72+8, y*scale*.86+13, x*scale*2.2+21], dtype="f")
    return map_coordinates(grids[index], coords, order=1, mode="grid-wrap", prefilter=False)


bend = noise(x, y, z, 2, 0)
coarse = noise(x+.07*bend, y+.22*bend, z+.16*bend, 3.2, 1)
fine = noise(x+.07*coarse, y+.12*bend, z, 6.2, 2)
thread = noise(x+.07*bend, y+.18*coarse, z, 13.5, 3)
spine = y + .39*x - .17*np.sin(4*x + 1.8*z) - .19*bend
ribbon = np.exp(-4.8*(spine-.12*z)**2-1.4*(z+.28*np.sin(3*x))**2)
filaments = np.exp(-3.8*np.abs(.40*coarse+.20*fine+spine*.68+.10*thread))
density = ribbon*np.maximum(0, .18+.63*coarse+.33*fine+.15*thread+.35*filaments)**1.6
edge = np.clip((1-np.abs(x))/.17, 0, 1)*np.clip((1-np.abs(y))/.25, 0, 1)
density *= edge*edge*(3-2*edge)
# Optical depth toward the warm star at the upper right. The cold rim gets the ambient sky light.
shadow = np.zeros((h,w), dtype="f")
sunlight = np.empty_like(density)
for k in range(depth-1,-1,-1):
    shadow = np.roll(shadow, (1,-2), axis=(0,1))
    shadow[0,:] = 0
    shadow[:,-2:] = 0
    shadow += density[k]*.074
    sunlight[k] = np.exp(-shadow)
warm = np.clip(.28+.35*x+.35*coarse+.34*sunlight,0,1)
atlas = np.zeros((2*h,4*w,4), dtype="uint8")
for slab in range(8):
    emission = np.zeros((h,w,3), dtype="f")
    transmission = np.ones((h,w), dtype="f")
    for k in range(slab*12,(slab+1)*12):
        a = -np.expm1(-density[k]*.055)
        ink = (np.array([.39,.47,.78]) + warm[k,:,:,None]*np.array([.60,.22,-.27]))
        ink *= (.54+.54*sunlight[k,:,:,None])
        emission += (transmission*a)[:,:,None]*np.minimum(1,ink)
        transmission *= 1-a
    alpha=1-transmission
    rgba=np.dstack((emission/np.maximum(alpha[:,:,None],1e-8),alpha))
    row,col=divmod(slab,4)
    atlas[row*h:(row+1)*h,col*w:(col+1)*w]=np.uint8(np.clip(np.rint(rgba*255),0,255))
Image.fromarray(atlas).save(Path(__file__).with_name("corona-volume.png"),optimize=True)
