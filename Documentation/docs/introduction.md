# What is MOSAIC?

MOSAIC is an application for building experiments with signals from the human body.
It brings receiving data, processing it, viewing the results and recording them into
one workspace. You assemble an experiment by connecting small processing units on
screen, then watch how the data changes as it passes through them.

It is intended for researchers, students and developers working with signals such as
muscle activity, body motion and ultrasound. For example, you might want to compare
ways of measuring muscle activity, collect examples of different movements, or use
a learned movement estimate to control a virtual hand.

You can begin with the operations already included in MOSAIC. Writing a new operation
is an option when your experiment needs something the existing ones do not provide.

## An example: watching muscle activity

Suppose you want to see how activity in a muscle changes as someone contracts and
relaxes it. An electromyography (**EMG**) sensor measures electrical activity associated
with the muscle and sends a stream of measurements to the computer.

In MOSAIC, you could build this experiment in four steps:

1. **Receive the measurements.** A device connection supplies the sensor's channel values.
   Each channel represents one signal measured by the device.
2. **Filter the signal.** A filter reduces selected frequency components so you can
   examine the part of the signal relevant to your experiment.
3. **Calculate an activity measure.** Group a short history of samples and calculate
   their average absolute value. This produces a changing measure of signal amplitude,
   which is easier to follow than the rapidly oscillating raw waveform.
4. **View and record the result.** Compare the incoming signal with the activity measure
   while the experiment runs, and save data for later analysis.

The raw waveform and the activity measure answer different questions. The first shows
what the sensor supplied; the second summarizes that signal over a period of time.
MOSAIC lets you inspect both within the same experiment.

This is one possible experiment, not a required sequence. You can stop at viewing the
raw signal, compare several processing methods, or continue into learning and control.

## How you assemble an experiment

Each unit that performs a job is called a **block**. A device block receives data,
a filter block transforms it, and a feature block calculates a summary such as the
activity measure above.

You place blocks in the workspace and connect an output to the next block's input.
A connection means “send the values produced here to this operation.” The connected
arrangement is called a **pipeline**.

A block's output can go to more than one destination. For example, the same sensor
stream can feed two filters so you can compare their results without repeating the
acquisition. Each block has its own settings, such as a filter cutoff or the length
of history used to calculate a feature.

The application calls the list of available blocks the **palette**, and the area
where you connect them the **canvas**. Opening a block's **card** gives you its controls
and, when available, its live displays. These are different views of the same experiment.

## What happens while it runs

Start the source that supplies your data. As measurements arrive, connected blocks
process them and pass their results onward. The experiment continues to operate while
you inspect a plot; showing or hiding a plot does not start or stop acquisition.

The values do not have to keep the same form throughout the pipeline. A source can
provide many samples per second, while a later block produces one activity measure
from each group of samples. A learning block can turn those measures into a predicted
class or a control value. Understanding what each output represents is part of
designing the experiment.

You choose how far the pipeline goes. It can end with data you inspect and record,
or send results to another application or a connected device.

## What you can save and reuse

MOSAIC separates the **experiment setup** from the **data it produces**.

Saving the pipeline preserves the block arrangement and exported settings, so you can
reopen it or share the setup. Recording saves the values published by the blocks you
select. For example, you can record both the sensor output and the derived activity
measure to compare them later.

A saved pipeline does not contain the entire live session. Device connections need
to be established again, and training data or fitted models require the persistence
features of the particular learning block.

## Start with a signal you already understand

You do not need a sensor to learn MOSAIC. Its signal generator produces a known waveform,
so you can first learn to connect blocks and recognize the expected output. You can
then apply the same ideas to a supported device.

MOSAIC has been run on Windows, macOS and iOS. Device availability depends on the
platform and the device's drivers or libraries.

Continue with **[Getting Started](getting-started.md)** to set up the application.
If MOSAIC is already running, take a short **[tour of the workbench](workbench.md)**,
then build **[Your First Pipeline](first-pipeline.md)**.
