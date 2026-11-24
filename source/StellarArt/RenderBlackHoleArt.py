"""Bake the stationary shadow, orbiting gas and fine arcs of the black-hole corona."""
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent
TEXEL_SCALE = 2
PIXELS_PER_UNIT = 108 * TEXEL_SCALE


def save_rgba(name, rgb, alpha):
    rgba = np.dstack((rgb, alpha))
    rgba[alpha < .5 / 255] = 0
    Image.fromarray(np.uint8(np.rint(np.clip(rgba, 0, 1) * 255))).save(OUT / name, optimize=True)


def smooth(a, b, v):
    t = np.clip((v - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def render_material():
    side = 544 * TEXEL_SCALE
    y, x = np.mgrid[:side, :side]
    x = (x + .5) * 2 / side - 1
    y = (y + .5) * 2 / side - 1
    r, a = np.hypot(x, y), np.arctan2(y, x)
    spiral = a - 2.1 * np.log(np.maximum(.20, r))
    knots = np.clip(.5+.27*np.cos(2*spiral+.8*np.sin(3*a))+.17*np.cos(5*spiral-3*r)+.13*np.sin(3*spiral),0,1)**1.3
    eddy = .5 + .5 * np.sin(12*spiral+2*np.cos(3*a+8*r))
    folds = .5 + .5 * np.sin(69*r+2.9*np.sin(3*spiral)+1.55*np.sin(6*a-9*r))
    veins = folds ** 5
    bounds = smooth(.19, .40, r) * (1-smooth(.76, .99, r))
    heat = np.clip(.07 + .76*knots + .21*veins + .12*eddy, 0, 1)
    shadow = np.array([.34, .102, .14])
    gold = np.array([1.00, .72, .40])
    rgb = shadow + (gold-shadow)*heat[..., None]
    blue = smooth(.42,.92,.5+.5*np.sin(spiral+.7*np.sin(3*a)))*smooth(.45,.83,r)
    rgb += blue[...,None]*np.array([-.13,.12,.47])
    rgb += (knots*(.25+.75*veins))[..., None]*np.array([.34,.56,.76])
    alpha = bounds*(.28+.56*knots+.30*veins)
    save_rgba("accretion-flow.png", rgb, np.clip(alpha,0,1))

    # A face-on map for the lensed inner flow. Thin irregular arcs travel around the
    # shadow without rotating its outline or changing the tilt of the equatorial disk.
    ridge = .757+.016*np.sin(3*a+.5)+.009*np.sin(8*a)
    filament = np.exp(-((r-ridge)/.029)**2)
    companion = np.exp(-((r-ridge-.060-.014*np.cos(5*a))/.046)**2)
    patches = (.5+.5*np.cos(3*a-.62*np.sin(5*a)))**1.8
    wisps = (.5+.5*np.cos(9*a+.7*np.sin(3*a)))**2
    alpha = (.80*filament + .46*companion + .20*np.exp(-((r-ridge)/.126)**2))*(.20+.80*patches)
    warm = np.clip(.32+.55*patches+.23*wisps,0,1)
    rgb = np.array([.91,.31,.18])+warm[...,None]*np.array([.12,.61,.53])
    save_rgba("photon-flow.png", rgb, np.clip(alpha,0,1))

    # Stretched, broken stellar images in the outer annulus. The asymmetric gaps
    # let the true, remapped sky underneath show through between caustics.
    light=np.zeros(r.shape+(3,))
    def arc(radius, width, angle, span, color, gain):
        angular=np.arctan2(np.sin(a-angle),np.cos(a-angle))
        envelope=np.exp(-.5*(angular/span)**2)
        center=radius+.015*np.sin(a*3+angle)+.008*np.cos(a*7-2*angle)
        radial=np.exp(-((r-center)/width)**2)
        dust=.68+.21*np.cos(10*a+radius*13)+.11*np.sin(24*a-angle)
        light[:]+=gain*(radial*envelope*dust)[...,None]*np.array(color)

    for radius,width,angle,span,color,power in [
        (.55,.025,-2.26,.62,(.42,.76,1),1.25),(.598,.012,-2.1,.46,(.87,.80,1),1.5),
        (.65,.006,-2.02,.25,(.22,.62,1),1.45),(.70,.014,-.71,.24,(.66,.48,1),.79),
        (.69,.007,-.84,.13,(.76,.91,1),1.90),(.59,.027,.71,.54,(.99,.47,.23),.86),
        (.65,.008,.83,.32,(1,.83,.50),1.2),(.705,.013,2.01,.28,(.42,.67,1),.78),
        (.74,.006,2.13,.20,(.70,.91,1),1.35),(.84,.010,3.03,.15,(.54,.69,1),.75)]:
        arc(radius,width,angle,span,color,power)
    light+=(.14*np.exp(-((r-.615)/.11)**2)*(.47+.53*np.cos(a+1.9)**2))[...,None]*np.array([.40,.43,1.0])
    emission=1-np.exp(-light);alpha=emission.max(axis=2)
    save_rgba("lens-arcs.png",emission/np.maximum(alpha[...,None],1e-8),alpha)


def render():
    width, height = 840 * TEXEL_SCALE, 544 * TEXEL_SCALE
    y, x = np.mgrid[:height, :width]
    x = (x + .5 - width / 2) / PIXELS_PER_UNIT
    y = (y + .5 - height / 2) / PIXELS_PER_UNIT
    tilt = np.radians(-11)
    u = x * np.cos(tilt) + y * np.sin(tilt)
    v = -x * np.sin(tilt) + y * np.cos(tilt)
    radius = np.hypot(u, v)
    angle = np.arctan2(v, u)
    light = np.zeros(x.shape + (3,))

    def emit(energy, color):
        light[:] += np.maximum(0, energy)[..., None] * np.array(color)

    # Behind the shadow the inner disc appears again as a bright lensed arc.
    # Keep the underside fainter and the approaching limb asymmetrically bright.
    arc_r = np.hypot(u, v * np.where(v < 0, 1.0, 1.19))
    beam = 1 - .44 * np.cos(angle - .20)
    ripples = .74 + .26 * np.sin(arc_r * 83 + 1.7 * np.sin(angle * 4)) ** 2
    emit(.67 * np.exp(-((arc_r-1.18)/.20)**2) * beam * ripples, (1.0, .48, .25))
    emit(.90 * np.exp(-((arc_r-1.105)/.059)**2) * beam, (1.0, .79, .59))
    emit(.30 * np.exp(-((arc_r-1.31)/.29)**2) * beam, (.50, .38, .71))
    emit(.37 * np.exp(-((radius-.994)/.019)**2), (1.0, .87, .69))
    warped = np.sqrt(np.maximum(0, 1-(u/1.42)**2))
    arc_y = .11 - 1.32 * warped
    arc_edge = np.clip((1.42-np.abs(u))/.18, 0, 1)
    emit(.41*np.exp(-((v-arc_y)/.10)**2)*arc_edge*beam, (.99,.63,.46))

    rear_light = light
    light = np.zeros_like(rear_light)
    # The near face occults the far face, even where the two share a screen pixel.
    # Keep the two plates separate so animated material obeys the same depth order.
    midline = .12 + .18 * np.exp(-((u+.08)/1.42)**2) - .020 * u
    dy = v - midline
    spread = .07 + .12 * np.sqrt(np.maximum(0, 1 - (u / 3.17) ** 2))
    radial = np.sqrt((u*.68)**2 + (dy/spread*.66)**2)
    outer = np.clip(1 - (np.abs(u)/3.46)**2, 0, 1) ** 1.1
    disk_beam = (1.12 - .22 * u) * outer
    filaments = .68 + .32 * np.sin(dy/spread * 16 + radial*3 + .9*np.sin(u*3.5))**2
    emit(.48 * disk_beam * np.exp(-(dy/(spread*2.0))**2), (.72,.32,.35))
    emit(1.85 * disk_beam * filaments * np.exp(-(dy/spread)**2), (1.0,.68,.40))
    emit(2.05 * disk_beam * np.exp(-((dy+spread*.12)/(spread*.21))**2), (1.0,.95,.84))
    emit(.28 * outer * np.exp(-((dy-spread*.9)/(spread*.20))**2), (.66,.72,1.0))
    for offset in (-1.49, -1.13, .76, 1.27):
        ribbon = (offset + .14*np.sin(4*u + offset)) * spread
        emit(.34 * disk_beam * np.exp(-((dy-ribbon)/(.018+.004*abs(offset)))**2), (.93,.68,.48))

    # Outside the optical ring the thin outer disk is emissive and transparent.
    # The dense near lane only needs a separate plate where the projected faces meet.
    crossing = 1 - smooth(1.62, 2.12, np.abs(u))
    rear_light += light*(1-crossing[...,None])
    # The shadow stays at the gate. Emit its matte separately so the hot material
    # can travel across the window without carrying a second black silhouette.
    transmission = 1 - np.clip((1.040 - arc_r) / .059, 0, 1)
    # Leave exposure for the moving plasma over this fixed silhouette.
    color = 1 - np.exp(-.74*rear_light)
    glow = np.max(color, axis=-1)
    save_rgba("black-hole.png", np.broadcast_to([.009,.014,.033], color.shape), 1-transmission)
    save_rgba("ring-emission.png", color / np.maximum(glow[...,None], 1e-7), glow)

    # Optical depth across the projected near disk. Its bright lane is optically
    # thick and the wispy edges transmit the light behind them progressively.
    front_color = 1 - np.exp(-.74*light*crossing[...,None])
    density = crossing * outer * np.exp(-.5*(dy/(.43*spread))**2)
    front_alpha = 1 - (1-front_color.max(axis=2))*np.exp(-6.0*density)
    save_rgba("front-disk.png", front_color / np.maximum(front_alpha[...,None], 1e-7), front_alpha)
    emission = 1 - np.exp(-.74*light)
    return emission[...,0]*.58 + emission[...,1]*.42


def doppler_material(emission):
    h, w = emission.shape
    y, x = np.mgrid[:h, :w]
    x = (x + .5 - w / 2) / PIXELS_PER_UNIT
    y = (y + .5 - h / 2) / PIXELS_PER_UNIT
    angle = np.radians(-11)
    u, v = x * np.cos(angle) + y * np.sin(angle), -x * np.sin(angle) + y * np.cos(angle)
    mid = .12 + .18 * np.exp(-((u+.08)/1.42)**2) - .020*u
    disk = np.exp(-.5*((v-mid)/.215)**2) * smooth(.38,1.18,np.abs(u))
    for name, side, low, high in [
        ("upload-glow.png", u>0, [.16,.48,1.0], [.76,.95,1.0]),
        ("download-glow.png", u<0, [.73,.15,.12], [1.0,.53,.36])
    ]:
        weight = (disk * emission**.64 * side).clip(0,1)
        rgb = low + (np.array(high)-low)*emission[...,None]**1.7
        save_rgba(name,rgb,weight)


if __name__ == "__main__":
    disk_emission = render()
    render_material()
    doppler_material(disk_emission)
