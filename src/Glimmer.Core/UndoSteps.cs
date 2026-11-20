// Ported from Packages/GlimmerKit/Sources/GlimmerCore/UndoSteps.swift
//
// The Swift file extends Foundation's UndoManager (separateStep). Windows has no UndoManager, so this file also holds
// the part of NSUndoManager the app relies on: groups opened automatically per user event (groupsByEvent), explicit
// groups, undo and redo stacks, action names and a limit (levelsOfUndo). The app ends each user event with
// CloseEventGroup(), as AppKit's run loop does after every event.

namespace Glimmer.Core;

/// <summary>An undo stack with the behaviour of AppKit's <c>NSUndoManager</c> that Glimmer depends on.</summary>
/// <remarks>Not thread-safe: use it from the UI thread, as on the Mac.</remarks>
public sealed class UndoManager
{
    private sealed class Group
    {
        /// <summary>Actions and nested groups, in the order they were registered.</summary>
        public readonly List<object> Items = [];
        public string ActionName = "";
        public bool Automatic;
        public bool IsEmpty => Items.Count == 0;
    }

    private sealed record Action(object? Target, System.Action Run);

    private readonly List<Group> undoStack = [];
    private readonly List<Group> redoStack = [];
    private readonly Stack<Group> open = new();
    private int registrationDisabled;

    /// <summary>
    /// Whether the first registration of a user event opens a group that the end of the event closes (on by default).
    /// Changes made outside an event must use <see cref="SeparateStep"/>.
    /// </summary>
    public bool GroupsByEvent { get; set; } = true;

    /// <summary>The most undo steps kept (0 = no limit). Lowering it drops the oldest steps at once.</summary>
    public int LevelsOfUndo
    {
        get;
        set
        {
            field = Math.Max(0, value);
            Trim(undoStack);
            Trim(redoStack);
        }
    }

    public bool IsUndoing { get; private set; }
    public bool IsRedoing { get; private set; }

    /// <summary>How many groups are open (0 when none).</summary>
    public int GroupingLevel => open.Count;

    public bool CanUndo => undoStack.Count > 0 || (open.Count == 1 && !open.Peek().IsEmpty && !IsUndoing && !IsRedoing);
    public bool CanRedo => redoStack.Count > 0;

    public string UndoActionName => open.Count > 0 && !open.Peek().IsEmpty && !IsUndoing && !IsRedoing
        ? open.Peek().ActionName
        : undoStack.Count > 0 ? undoStack[^1].ActionName : "";

    public string RedoActionName => redoStack.Count > 0 ? redoStack[^1].ActionName : "";

    public bool IsUndoRegistrationEnabled => registrationDisabled == 0;
    public void DisableUndoRegistration() => registrationDisabled++;
    public void EnableUndoRegistration() => registrationDisabled = Math.Max(0, registrationDisabled - 1);

    // MARK: Grouping

    public void BeginUndoGrouping() => BeginGroup(automatic: false);

    private void BeginGroup(bool automatic)
    {
        var group = new Group { Automatic = automatic };
        if (open.Count > 0) open.Peek().Items.Add(group);
        open.Push(group);
    }

    /// <exception cref="InvalidOperationException">No group is open.</exception>
    public void EndUndoGrouping()
    {
        if (open.Count == 0) throw new InvalidOperationException("endUndoGrouping called with no matching begin.");
        var group = open.Pop();
        if (open.Count > 0)
        {
            // A nested group stays inside its parent; an empty one is dropped.
            if (group.IsEmpty) open.Peek().Items.Remove(group);
            return;
        }
        if (group.IsEmpty) return;
        var stack = IsUndoing ? redoStack : undoStack;
        stack.Add(group);
        Trim(stack);
    }

    /// <summary>
    /// What AppKit does at the end of every user event while <see cref="GroupsByEvent"/> is on: closes the group the
    /// event's first registration opened. The app calls this after each input event; nothing else needs to.
    /// </summary>
    public void CloseEventGroup()
    {
        if (open.Count == 1 && open.Peek().Automatic) EndUndoGrouping();
    }

    // MARK: Registering

    /// <summary>Records <paramref name="handler"/> as the way back, to run on undo (or on redo while undoing).</summary>
    public void RegisterUndo<TTarget>(TTarget target, Action<TTarget> handler) where TTarget : class =>
        Register(new Action(target, () => handler(target)));

    public void RegisterUndo(System.Action handler) => Register(new Action(null, handler));

    private void Register(Action action)
    {
        if (registrationDisabled > 0) return;
        if (open.Count == 0)
        {
            if (!GroupsByEvent && !IsUndoing && !IsRedoing)
                throw new InvalidOperationException("An undo action was registered with no group open and automatic grouping off.");
            BeginGroup(automatic: GroupsByEvent && !IsUndoing && !IsRedoing);
        }
        // A new change outside undo and redo drops what could be redone.
        if (!IsUndoing && !IsRedoing) redoStack.Clear();
        open.Peek().Items.Add(action);
    }

    /// <summary>Names the step being built, or else the last one (shown as "Undo &lt;name&gt;").</summary>
    public void SetActionName(string name)
    {
        if (open.Count > 0)
        {
            foreach (var g in open) g.ActionName = name;
            return;
        }
        var stack = IsUndoing ? redoStack : undoStack;
        if (stack.Count > 0) stack[^1].ActionName = name;
    }

    // MARK: Undo and redo

    /// <summary>Undoes the last step; an automatic group still open is closed first.</summary>
    /// <exception cref="InvalidOperationException">An explicit group is open.</exception>
    public void Undo()
    {
        if (open.Count == 1 && open.Peek().Automatic) EndUndoGrouping();
        if (open.Count > 0) throw new InvalidOperationException("undo was called with an undo group open.");
        if (undoStack.Count == 0) return;
        var group = undoStack[^1];
        undoStack.RemoveAt(undoStack.Count - 1);
        Replay(group, undoing: true);
    }

    public void Redo()
    {
        if (open.Count > 0) throw new InvalidOperationException("redo was called with an undo group open.");
        if (redoStack.Count == 0) return;
        var group = redoStack[^1];
        redoStack.RemoveAt(redoStack.Count - 1);
        Replay(group, undoing: false);
    }

    /// <summary>Runs a group's actions newest first, gathering what they register as one opposite step with the same name.</summary>
    private void Replay(Group group, bool undoing)
    {
        if (undoing) IsUndoing = true;
        else IsRedoing = true;
        BeginGroup(automatic: false);
        open.Peek().ActionName = group.ActionName;
        try
        {
            Run(group);
        }
        finally
        {
            var opposite = open.Pop();
            while (open.Count > 0) open.Pop();
            if (!opposite.IsEmpty)
            {
                opposite.ActionName = group.ActionName;
                var stack = undoing ? redoStack : undoStack;
                stack.Add(opposite);
                Trim(stack);
            }
            IsUndoing = false;
            IsRedoing = false;
        }
    }

    private static void Run(Group group)
    {
        for (int i = group.Items.Count - 1; i >= 0; i--)
        {
            switch (group.Items[i])
            {
                case Action a: a.Run(); break;
                case Group g: Run(g); break;
            }
        }
    }

    private void Trim(List<Group> stack)
    {
        if (LevelsOfUndo > 0 && stack.Count > LevelsOfUndo) stack.RemoveRange(0, stack.Count - LevelsOfUndo);
    }

    /// <summary>Forgets every step.</summary>
    public void RemoveAllActions()
    {
        undoStack.Clear();
        redoStack.Clear();
        open.Clear();
    }

    /// <summary>Forgets the steps' actions that were registered for <paramref name="target"/>.</summary>
    public void RemoveAllActions(object target)
    {
        static void Strip(Group g, object target)
        {
            g.Items.RemoveAll(item => item is Action a && ReferenceEquals(a.Target, target));
            foreach (var nested in g.Items.OfType<Group>()) Strip(nested, target);
            g.Items.RemoveAll(item => item is Group n && n.IsEmpty);
        }
        foreach (var stack in new[] { undoStack, redoStack })
        {
            foreach (var g in stack) Strip(g, target);
            stack.RemoveAll(g => g.IsEmpty);
        }
    }

    /// <summary>
    /// Registers what <paramref name="register"/> adds as one undo step of its own.
    /// </summary>
    /// <remarks>
    /// With <see cref="GroupsByEvent"/> (the default), the undo manager opens a group on the first registration and
    /// closes it only after a user event. Changes made by the agent API, or by work that finishes after its event (an
    /// assistant plan), aren't events: several of them landed in one open group, and one Undo took them all back. So
    /// such a change gets an explicit group with automatic grouping off, and never opens an automatic one. Every change
    /// made outside an event must come through here. (Closing a leftover automatic group by hand doesn't work on the
    /// Mac: the next undo() closes it again and throws.) Never while undoing or redoing, when the manager builds the
    /// opposite step itself.
    /// </remarks>
    public void SeparateStep(System.Action register)
    {
        if (IsUndoing || IsRedoing)
        {
            register();
            return;
        }
        bool automatic = GroupsByEvent;
        // An explicit group opened while groupsByEvent is on is taken for the automatic one, and the next undo() closes
        // it a second time (an NSInternalInconsistencyException on the Mac).
        GroupsByEvent = false;
        BeginUndoGrouping();
        try
        {
            register();
        }
        finally
        {
            EndUndoGrouping();
            GroupsByEvent = automatic;
        }
    }
}
