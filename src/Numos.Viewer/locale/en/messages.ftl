unhandled-exception = Unhandled exception
unhandled-exception-message = Unhandled exception: { $exception }
unobserved-task-exception = Unobserved task exception
draw-data-build-failed = Could not build the draw data
source-runtime = Runtime
exception-message = { $context }: { $message }

open-messages-logs = Open Messages / Logs.

error-count =
    { $count ->
        [one] { $count } error
       *[other] { $count } errors
    }
warning-count =
    { $count ->
        [one] { $count } warning
       *[other] { $count } warnings
    }

filter-messages = Filter messages...
auto-scroll = Auto-scroll
copy = Copy
clear = Clear
messages-count-summary = { $shown } shown / { $total } total

time = Time
tick = Tick
level = Level
source = Source
message = Message
icon-sheet-error = Cannot load the icon sheet { $path }: { $reason }. Icons are hidden.
icon-sheet-missing = the file does not exist
icon-sheet-size = expected at least { $width } x { $height } pixels, found { $actualWidth } x { $actualHeight }
