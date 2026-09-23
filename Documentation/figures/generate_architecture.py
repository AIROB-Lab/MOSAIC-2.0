"""Editable architecture artwork; run with Python + matplotlib. Coordinates are mm."""
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'output/figures'
OUT.mkdir(parents=True, exist_ok=True)
PDF = ROOT / 'output/pdf'
PDF.mkdir(parents=True, exist_ok=True)
plt.rcParams.update({'font.family': 'DejaVu Sans', 'svg.fonttype': 'none',
                     'pdf.fonttype': 42, 'font.size': 8})


def draw(dark=False):
    bg = '#1f1f23' if dark else '#ffffff'
    fg = '#e1e1e1' if dark else '#173047'
    muted = '#b9c4d2' if dark else '#526473'
    line = '#617282' if dark else '#b5c4ce'
    panel = '#25272d' if dark else '#f5f8fa'
    fill = '#30353d' if dark else '#ffffff'
    cyan = '#42c8ff' if dark else '#007fad'
    observe = '#f0bc66' if dark else '#9d670f'
    fig = plt.figure(figsize=(180/25.4, 140/25.4), facecolor=bg)
    ax = fig.add_axes([0, 0, 1, 1], xlim=(0, 180), ylim=(0, 140))
    ax.set_axis_off()

    def text(x, y, s, size=8, color=fg, weight='normal', ha='center', va='center'):
        return ax.text(x, y, s, fontsize=size, color=color, weight=weight,
                       ha=ha, va=va, linespacing=1.45)

    def rect(x, y, w, h, color=fill, edge=line, radius=1.4, lw=.7):
        p=FancyBboxPatch((x,y),w,h,boxstyle=f'round,pad=0,rounding_size={radius}',
                        facecolor=color,edgecolor=edge,linewidth=lw)
        ax.add_patch(p)

    def box(x,y,w,h,title,sub=None):
        rect(x,y,w,h)
        text(x+w/2,y+h*(.65 if sub else .5),title,8,weight='bold')
        if sub:text(x+w/2,y+h*.28,sub,7,color=muted)

    def arrow(points,kind='data',both=False):
        c={'data':cyan,'config':fg,'observe':observe}[kind]
        ls={'data':'-','config':(0,(4,3)),'observe':(0,(1.4,2))}[kind]
        for a,b in zip(points[:-2],points[1:-1]):
            ax.plot([a[0],b[0]],[a[1],b[1]],color=c,lw=1,linestyle=ls)
        ax.add_patch(FancyArrowPatch(points[-2],points[-1],arrowstyle='<->' if both else '-|>',
                      mutation_scale=8,linewidth=1,color=c,linestyle=ls,
                      shrinkA=0,shrinkB=0))

    def heading(letter,title,y):
        text(5,y,letter,10,weight='bold',ha='left',color=cyan)
        text(12,y,title,9,weight='bold',ha='left')

    heading('A','Compose and configure',133)
    for x,title,sub in [(5,'Block catalogue','names · parameter hints'),
                        (49,'Workbench','canvas · controls'),
                        (93,'Pipeline JSON','blocks · connections'),
                        (137,'Graph construction','instantiate · connect')]:
        box(x,112,38,15,title,sub)
    arrow([(43,119.5),(49,119.5)],'config')
    arrow([(87,119.5),(93,119.5)],'config',both=True)
    arrow([(131,119.5),(137,119.5)],'config')
    text(5,107,'Extend with a new block: processing implementation + factory registration + catalogue metadata',6.8,ha='left',color=muted)

    heading('B','Execute a configurable signal graph',99)
    rect(5,68,170,25,color=panel)
    for x,title,sub in [(9,'Acquisition','hardware / synthetic'),
                        (52,'Processing','filter · features'),
                        (95,'Learning','train · predict'),
                        (138,'Output','device · network')]:
        box(x,74,33,14,title,sub)
    for x in [42,85,128]:arrow([(x,81),(x+10,81)])
    arrow([(156,112),(178,112),(178,93),(172,93)],'config')
    text(5,64,'Representative pipeline: stages can branch, merge, or be omitted.',6.8,ha='left',color=muted)

    heading('C','Shared execution model',57)
    rect(5,29,142,23,color=panel)
    text(9,48.5,'Each processing block',7.2,ha='left',weight='bold')
    for x,t in [(9,'Receive'),(43,'Process'),(77,'Publish')]:box(x,33,26,11,t)
    arrow([(35,38.5),(43,38.5)])
    arrow([(69,38.5),(77,38.5)])
    box(116,33,28,11,'Dispatch','per-edge queue')
    box(155,33,20,11,'Next block')
    arrow([(103,38.5),(116,38.5)])
    arrow([(144,38.5),(155,38.5)])
    text(130,48.5,'per-edge ordering',7,color=muted)

    box(34,7,46,14,'Live visualization','buffered data → UI refresh')
    box(102,7,46,14,'Recording','background CSV writer')
    arrow([(56,33),(56,21)],'observe')
    arrow([(90,33),(90,25),(125,25),(125,21)],'observe')

    # The line styles carry meaning independently of color.
    for x,kind,label in [(6,'data','signal data'),(58,'config','configuration'),(116,'observe','observation / export')]:
        arrow([(x,2.8),(x+9,2.8)],kind)
        text(x+12,2.8,label,6.4,ha='left',color=muted)

    basename='mosaic-architecture-dark' if dark else 'mosaic-architecture'
    fig.savefig(OUT/f'{basename}.svg',facecolor=bg)
    fig.savefig(OUT/f'{basename}.png',dpi=300,facecolor=bg)
    if not dark:fig.savefig(PDF/'mosaic-architecture.pdf',facecolor=bg)
    else:fig.savefig(ROOT/'Documentation/images/mosaic-architecture.svg',facecolor=bg)
    plt.close(fig)


if __name__=='__main__':
    draw()
    draw(dark=True)
    print('Created paper SVG, PNG and PDF; matching documentation SVG and PNG.')
