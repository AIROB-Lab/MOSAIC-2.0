"""Scope time base and optional window overlap. Editable SVG/PDF and 300 dpi PNG."""
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch, Rectangle

ROOT = Path(__file__).resolve().parents[2]
OUT, PDF = ROOT / 'output/figures', ROOT / 'output/pdf'
OUT.mkdir(parents=True, exist_ok=True)
PDF.mkdir(parents=True, exist_ok=True)
plt.rcParams.update({'font.family':'DejaVu Sans','svg.fonttype':'none','pdf.fonttype':42})
FS, N = 2000, 400


def draw(dark=False):
    bg = '#1f1f23' if dark else '#ffffff'
    fg = '#e1e1e1' if dark else '#173047'
    muted = '#b9c4d2' if dark else '#526473'
    grid = '#617282' if dark else '#b5c4ce'
    panel = '#292b30' if dark else '#f5f8fa'
    cyan = '#42c8ff' if dark else '#007fad'
    gold = '#f0bc66' if dark else '#9d670f'
    old = '#474d56' if dark else '#dce3e9'
    fig = plt.figure(figsize=(180/25.4,174/25.4),facecolor=bg)
    ax = fig.add_axes([0,0,1,1],xlim=(0,180),ylim=(0,174))
    ax.set_axis_off()

    def text(x,y,s,size=8,ha='left',color=fg,weight='normal'):
        ax.text(x,y,s,fontsize=size,ha=ha,va='center',color=color,
                weight=weight,linespacing=1.4)

    def box(x,y,w,h,edge=grid):
        ax.add_patch(FancyBboxPatch((x,y),w,h,boxstyle='round,pad=0,rounding_size=1.5',
                                   facecolor=panel,edgecolor=edge,linewidth=.8))

    text(6,167,'From window updates to a continuous scope trace',10,weight='bold')
    text(6,160,'Example: 2,000 samples/s, 400 rows per window, one channel shown',7.5,color=muted)
    box(6,140,80,14,cyan)
    box(94,140,80,14,gold)
    text(46,149,'SignalRate = 2000',8.5,ha='center',color=cyan,weight='bold')
    text(46,143.5,'Sample spacing stays 0.5 ms.',7.2,ha='center')
    text(134,149,'DesiredRate = 2000 / stride',8.1,ha='center',color=gold,weight='bold')
    text(134,143.5,'Window update rate depends on stride.',7.2,ha='center')
    text(6,132,'What a downstream scope appends at each update',9,weight='bold')

    # Same horizontal time scale in both plots; two updates per scenario.
    for y, stride, title in [
        (84,400,'A   No overlap: stride 400, 5 windows/s'),
        (39,100,'B   Overlap: stride 100, 20 windows/s')]:
        rate = FS / stride
        retained = N - stride
        assert min(N,round(FS/rate)) == stride
        box(6,y,168,41)
        text(10,y+35,title,8.5,weight='bold')
        text(10,y+28,'Incoming window: 400 rows',7,color=muted)
        x,w,bar_y = 10,54,y+16
        if retained:
            ax.add_patch(Rectangle((x,bar_y),w*retained/N,8,facecolor=old,edgecolor=grid,lw=.5))
            text(x+w*retained/N/2,bar_y+4,'300 reused',6.5,ha='center',color=muted)
        ax.add_patch(Rectangle((x+w*retained/N,bar_y),w*stride/N,8,
                               facecolor=cyan,edgecolor=cyan,lw=.5))
        text(x+w*(retained+stride/2)/N,bar_y+4,str(stride),7,ha='center',color=bg,weight='bold')
        text(10,y+11,'Append all 400 rows.' if not retained else 'Append only the newest 100 rows.*',7)
        text(10,y+5,f'{stride} × 0.5 ms = {1000*stride/FS:g} ms per update',7,color=muted)
        ax.add_patch(FancyArrowPatch((66,y+20),(79,y+20),arrowstyle='-|>',
                                     mutation_scale=9,color=fg,linewidth=1))
        text(82,y+28,'Scope trace after two updates',7,color=muted)
        left, right, base = 83,168,y+8
        width = right-left
        duration_ms = 1000*stride/FS
        for i,color in enumerate([cyan,gold]):
            ax.add_patch(Rectangle((left+width*i*duration_ms/400,base+1),
                                   width*duration_ms/400,14,facecolor=color,alpha=.10,edgecolor='none'))
            t = np.arange(i*stride,(i+1)*stride)/FS
            wave = np.sin(2*np.pi*12*t) + .22*np.sin(2*np.pi*29*t)
            ax.plot(left+width*(t*1000)/400,base+8+4*wave,color=color,lw=.85)
        ax.plot([left,right],[base,base],color=grid,lw=.6)
        for tick in [0,100,200,300,400]:
            tx=left+width*tick/400
            ax.plot([tx,tx],[base-1,base],color=grid,lw=.5)
            text(tx,base-3,str(tick),6,ha='center',color=muted)
    text(168,37,'Elapsed plotted time (ms); same scale in both rows',6.3,ha='right',color=muted)

    text(6,29,'*Overlap trimming is enabled only when the visualization is given both rates.',7.1)
    text(6,23,'Scope time step = 1 / plotted sample rate; screen refresh is separate.',7.7,weight='bold')
    text(6,17,'Without an explicit sample rate: plotted rate = publish rate × rows fed to scope.',7,color=muted)
    text(6,11,'Example: one feature row at 20 updates/s → 20 points/s → 50 ms per point.',7,color=muted)
    text(6,5,"Sliding Window's own scope plots its input; the examples above concern downstream windows.",6.6,color=muted)

    stem='sample-rate-vs-window-rate'+('-dark' if dark else '')
    fig.savefig(OUT/f'{stem}.svg',facecolor=bg)
    fig.savefig(OUT/f'{stem}.png',facecolor=bg,dpi=300)
    if dark:fig.savefig(ROOT/'Documentation/images/sample-rate-vs-window-rate.svg',facecolor=bg)
    else:fig.savefig(PDF/'sample-rate-vs-window-rate.pdf',facecolor=bg)
    plt.close(fig)


if __name__=='__main__':
    draw()
    draw(dark=True)
    print('Updated scope figure: non-overlapping and overlapping windows, plus time-base derivation.')
