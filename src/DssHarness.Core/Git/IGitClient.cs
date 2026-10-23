using DssHarness.Core.Results;

namespace DssHarness.Core.Git;

/// <summary>
/// Access to git. Wraps the git command line rather than a library binding: the
/// harness depends on git's own worktree semantics, and git is already a hard
/// prerequisite of every repository it serves.
/// </summary>
/// <remarks>
/// Every path this interface returns comes from git in one resolved form, with symbolic
/// links followed, so paths from different calls can be compared with each other. A path
/// the caller typed is only ever used to tell git where to start.
/// </remarks>
public interface IGitClient
{
    /// <summary>Whether a usable git executable is on PATH.</summary>
    bool IsInstalled();

    /// <summary>Whether <paramref name="directory"/> is inside a work tree.</summary>
    /// <exception cref="HarnessException">
    /// git could not inspect the directory, for example because it refuses a repository
    /// owned by another user. That is not the same answer as "not a repository".
    /// </exception>
    Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Root of the work tree containing <paramref name="directory"/>, or <see langword="null"/>
    /// when it is not inside a repository. For a linked worktree this is the worktree's own
    /// root, not the main checkout.
    /// </summary>
    /// <exception cref="HarnessException">git could not inspect the directory.</exception>
    Task<string?> GetRepositoryRootAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// The repository's main worktree, even when <paramref name="directory"/> is inside a
    /// linked one, or <see langword="null"/> when it is not inside a repository - or when git
    /// names a git directory as the main worktree that records no checkout, as one made with
    /// --separate-git-dir does not, and <paramref name="directory"/> is in a linked worktree of
    /// it, where nothing says which checkout is the main one. This is where gitignored harness
    /// state (ssh secrets, the run lock) actually lives.
    /// </summary>
    /// <exception cref="HarnessException">git could not inspect the directory.</exception>
    Task<GitWorktree?> GetMainWorktreeAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>Whether the work tree has uncommitted changes, including untracked files.</summary>
    /// <exception cref="HarnessException">git could not read the status.</exception>
    Task<bool> IsDirtyAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Porcelain status entries, one per changed path; a rename or copy is one entry, with its new path and the one it
    /// was made from. Untracked files and changed submodules are listed even where configuration would hide them; an
    /// untracked directory is one entry, and an ignored file is none. A file the disk spells otherwise than the index is
    /// one entry, under the index's name, save the names <see cref="IndexedInAnotherCaseAsync"/> keeps.
    /// </summary>
    /// <exception cref="HarnessException">git could not read the status.</exception>
    Task<IReadOnlyList<GitStatusEntry>> GetStatusAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every path the work tree at <paramref name="directory"/> changes, as git names it: modified, staged or deleted;
    /// each file of an untracked directory, git's ignore rules followed - which a walk of the directory would not
    /// follow; and both the new and the old path of a rename or a copy. Asked with <c>-z</c>, so no path is ever
    /// quoted, and without git's optional locks, so asking writes nothing. A file the disk spells otherwise than the index
    /// is named once, under the index's name, save the names <see cref="IndexedInAnotherCaseAsync"/> keeps.
    /// </summary>
    /// <exception cref="HarnessException">git could not read the status.</exception>
    Task<IReadOnlyList<GitStatusEntry>> ReadStatusAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every tracked path whose working state under <paramref name="directory"/> differs from what <paramref name="commit"/>
    /// holds - its content, compared as git status compares it, the index's line-ending rules among them, or its mode
    /// where the repository trusts modes - its deletion included. An untracked file is not listed. Nothing is written.
    /// A set to ask of a path: a name that is not UTF-8 is no path this tool can ask about, and is not in it -
    /// <see cref="ListNamesChangedSinceAsync"/> lists every name.
    /// </summary>
    /// <exception cref="HarnessException">git could not compare the work tree with the commit.</exception>
    Task<IReadOnlySet<string>> ListChangedSinceAsync(string directory, string commit, CancellationToken cancellationToken = default);

    /// <summary>
    /// What <see cref="ListChangedSinceAsync"/> answers, every name as git holds it: a name that is not UTF-8 among them, for
    /// a caller that takes each name listed as a path to work with, and refuses one it cannot open.
    /// </summary>
    /// <exception cref="HarnessException">git could not compare the work tree with the commit.</exception>
    Task<IReadOnlyList<GitName>> ListNamesChangedSinceAsync(string directory, string commit, CancellationToken cancellationToken = default);

    /// <summary>
    /// What <paramref name="commit"/> holds at each of <paramref name="paths"/>: a file and its blob, a directory, a
    /// submodule's entry and the commit it names - whether or not this repository holds that commit, on every version of
    /// git - or nothing. One git process for them all, and one listing of the commit where git could not read one of them
    /// by its object: a path the commit does not hold, or a submodule's entry naming a commit this repository does not.
    /// </summary>
    /// <exception cref="HarnessException">
    /// git could not read the commit, answer for every path, or read a file the commit lists.
    /// </exception>
    Task<IReadOnlyDictionary<string, GitHeld>> HeldAtAsync(
        string directory,
        string commit,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The entry <paramref name="commit"/> holds at each of <paramref name="paths"/> - its mode among it, which says
    /// whether a file can be run and whether it is a symbolic link - asked for those paths alone; a path the commit holds
    /// nothing at is in no answer.
    /// </summary>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="paths">Paths from the repository's root, with forward separators, as git names them, each read literally.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not list the commit.</exception>
    Task<IReadOnlyDictionary<string, GitEntry>> ListEntriesAtAsync(
        string directory,
        string commit,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What the history of <paramref name="tip"/> held at each of <paramref name="paths"/>, newest first: every version
    /// found among the <paramref name="most"/> newest commits that changed any of them - a commit a merge dropped among
    /// them, since what it held was still that history's - each with a commit that holds it there; and whether that
    /// history goes further back than was read.
    /// </summary>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="tip">The commit whose history is read, or a name of it, read as <see cref="ResolveCommitAsync"/> reads it.</param>
    /// <param name="paths">Paths from the repository's root, with forward separators, as git names them, each read literally.</param>
    /// <param name="most">The most commits read: what bounds the cost, and what <see cref="GitPathHistory.Cut"/> says was hit.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not walk the history, or answer for every commit and path.</exception>
    Task<GitPathHistory> ListHeldInHistoryAsync(
        string directory,
        string tip,
        IReadOnlyList<string> paths,
        int most,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The blob each of <paramref name="paths"/> would be stored as at its own path in the work tree at
    /// <paramref name="directory"/>, in the same order: the one <c>git add</c> would record for its bytes as they stand -
    /// read through the clean filters and the line-ending rules of that path, its line endings kept where the work tree's
    /// index holds the path with carriage returns, as git keeps them, and never the entry's blob for a file the index
    /// marks unchanged. Nothing in the repository is written - neither its index nor its objects.
    /// </summary>
    /// <param name="directory">The work tree's root.</param>
    /// <param name="paths">Files in it, relative to the root, with forward separators.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not read a file, a filter of its path failed, or the path's attributes cannot be told to git.</exception>
    Task<IReadOnlyList<string>> ListStagedIdsAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Each of <paramref name="contents"/> as git would store it at <paramref name="path"/> in the work tree at
    /// <paramref name="directory"/>, in the same order: a blob's bytes as they are, and a file's or bytes in hand as
    /// <c>git add</c> would record them were they the file there - through the clean filters and the line-ending rules of
    /// that path, their line endings kept where the work tree's index holds the path with carriage returns, as git keeps
    /// them. Nothing in the repository is written - neither its index nor its objects.
    /// </summary>
    /// <param name="directory">The work tree's root, whose attributes and configuration say how the path is stored.</param>
    /// <param name="path">The path whose rules apply, relative to the root, with forward separators.</param>
    /// <param name="contents">The contents to read.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not read one of them as it would store it: a filter that failed, a blob it does not hold, or attributes of the path that cannot be told to it.</exception>
    Task<IReadOnlyList<byte[]>> ReadStagedAsync(string directory, string path, IReadOnlyList<GitContent> contents, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bytes git would write into the work tree at <paramref name="directory"/> for <paramref name="staged"/> checked
    /// out at <paramref name="path"/>: the smudge filters and the line-ending rules of that path applied. Nothing in the
    /// repository is written.
    /// </summary>
    /// <param name="directory">The work tree's root, whose attributes and configuration say how the path is checked out.</param>
    /// <param name="path">The path whose rules apply, relative to the root, with forward separators.</param>
    /// <param name="staged">The bytes as git stores them.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not check them out: a filter that failed, for one.</exception>
    Task<byte[]> ReadCheckedOutAsync(string directory, string path, byte[] staged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Three versions of one text merged as <c>git merge-file</c> merges them, each given as git stores it: clean, with
    /// the merged bytes and how many hunks each side changed; in conflict, counted and shown, each side under its name and
    /// what both held between them; or not merged at all, in git's own words, which is an answer and no failure of the
    /// question. No repository is read or written.
    /// </summary>
    /// <param name="ancestor">What both sides held.</param>
    /// <param name="ours">The side the merge is made into.</param>
    /// <param name="theirs">The side merged in.</param>
    /// <param name="names">What the three are called in a conflict's markers.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not be run, or the three could not be written for it to read.</exception>
    Task<GitTextMerge> MergeTextAsync(byte[] ancestor, byte[] ours, byte[] theirs, GitMergeNames names, CancellationToken cancellationToken = default);

    /// <summary>
    /// The objects <paramref name="objects"/> names, in order, each as its bytes, read by one git process;
    /// <see langword="null"/> for one that is no blob git can read.
    /// </summary>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="objects">Blob ids, or names git reads as one, none holding a line break.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git could not answer.</exception>
    Task<IReadOnlyList<byte[]?>> ReadBlobsAsync(string directory, IReadOnlyList<string> objects, CancellationToken cancellationToken = default);

    /// <summary>
    /// What git's attributes declare for each of <paramref name="paths"/> in the work tree at <paramref name="directory"/>:
    /// for each path, each of <paramref name="attributes"/> with what <c>git check-attr</c> answers for it - its value, or
    /// one of <see cref="GitAttributes"/>. Asked of git rather than parsed: git's own precedence between patterns,
    /// directories and the repository's info attributes is the answer, and a second reading of it would be a second answer.
    /// </summary>
    /// <param name="directory">The work tree's root.</param>
    /// <param name="attributes">The attributes to ask about.</param>
    /// <param name="paths">Paths relative to the root, with forward separators, whether or not a file is there.</param>
    /// <param name="unread">
    /// What git failing to answer leaves undone, as its refusal says it after "git check-attr failed, so"; null says that
    /// what the attributes declare for the paths could not be read.
    /// </param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not answer.</exception>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadAttributesAsync(
        string directory,
        IReadOnlyList<string> attributes,
        IReadOnlyList<string> paths,
        string? unread = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every worktree attached to the repository, the main worktree first.</summary>
    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where <paramref name="directory"/> sits in its repository, or <see langword="null"/> when it is
    /// not inside one.
    /// </summary>
    /// <exception cref="HarnessException">git could not inspect the directory.</exception>
    Task<GitLocation?> GetLocationAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// The index entries below <paramref name="directory"/>, with paths relative to it, and the flags
    /// that can hide an edit from status, as <c>ls-files --stage -v -z</c> lists them - a conflicted path once
    /// for each of its stages - without taking the index's lock.
    /// </summary>
    /// <exception cref="HarnessException">git could not read the index, or listed what this build cannot read.</exception>
    Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the index lists of <paramref name="paths"/> alone, as <see cref="ListIndexAsync(string, CancellationToken)"/>
    /// lists every entry: each path named as it is spelt, never read as a pattern, and given in batches no command line
    /// outgrows.
    /// </summary>
    /// <param name="directory">The directory git runs in, which the paths are relative to.</param>
    /// <param name="paths">The paths, with forward separators.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not read the index, or listed what this build cannot read.</exception>
    Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the index of the repository at <paramref name="directory"/> hold exactly <paramref name="paths"/>:
    /// each one's bytes as they stand on disk, and no entry they do not name. A path with no file at it is
    /// not held.
    /// </summary>
    /// <param name="directory">The top of the repository's own work tree.</param>
    /// <param name="paths">The files the index is to hold, relative to <paramref name="directory"/>, with forward separators.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">
    /// <paramref name="directory"/> is not the top of a repository of its own, or git could not read the
    /// files or write the index.
    /// </exception>
    /// <remarks>
    /// For a tree whose files some other process placed, and whose index is its record of which files
    /// are its own: a copy a sync made has its files written and none of them staged, and everything that
    /// reads "the files git tracks" there then reads none. The bytes are read through no filter and no
    /// line-ending conversion, and the index is built whole beside the one git reads and then put in its
    /// place, so neither a filter the machine cannot run nor a lock a stopped git left behind stops it.
    /// </remarks>
    Task IndexExactlyAsync(string directory, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the work tree at <paramref name="directory"/>, and its index, hold at each of <paramref name="paths"/> what
    /// <paramref name="commit"/> holds there, written through git's own filters and line-ending rules, and removes each
    /// one it does not hold: <c>git checkout --no-overlay</c>, every path read literally. No other path is touched, and
    /// HEAD does not move.
    /// </summary>
    /// <param name="directory">The work tree's root.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="paths">Paths relative to the root, with forward separators, as git names them.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git could not write them.</exception>
    Task CheckOutAtAsync(string directory, string commit, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of <paramref name="paths"/> the work tree at <paramref name="directory"/> holds as <paramref name="commit"/>
    /// holds them, each a file there: compared as git status would compare them were the commit's entry the index's - the
    /// content through the clean filters and the line-ending rules, and the mode where the repository trusts modes -
    /// whatever the work tree's own index says of the path, nothing included. A path the commit holds no file at is in no
    /// answer. Nothing in the repository is written.
    /// </summary>
    /// <param name="directory">The work tree's root.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="paths">Paths relative to the root, with forward separators, as git names them.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not list the commit or compare the files.</exception>
    Task<IReadOnlySet<string>> ListHeldAsAtAsync(string directory, string commit, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the HEAD of the work tree at <paramref name="directory"/> - the branch it is on, where it is on one - and its
    /// index to <paramref name="commit"/>, and leaves every file in the work tree as it is: <c>git reset --mixed</c>.
    /// </summary>
    /// <param name="directory">The work tree's root.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git could not move them.</exception>
    Task ResetToAsync(string directory, string commit, CancellationToken cancellationToken = default);

    /// <summary>The index file git uses for the work tree at <paramref name="directory"/>, as an absolute path.</summary>
    /// <exception cref="HarnessException">git could not say.</exception>
    Task<string> GetIndexFileAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which files marked assume-unchanged or skip-worktree differ from the index, as status would
    /// see them without the marks, line-ending rules included. The marks are cleared in
    /// <paramref name="indexCopy"/>, a copy of the work tree's index that git may change, never in the
    /// index itself. The paths are relative to <paramref name="directory"/>, and must exist on disk.
    /// </summary>
    /// <exception cref="HarnessException">git could not answer.</exception>
    Task<IReadOnlyList<string>> FindEditedFilesAsync(
        string directory,
        string indexCopy,
        IReadOnlyList<string> assumedUnchanged,
        IReadOnlyList<string> skipWorktree,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The git directory of the repository at <paramref name="path"/>, or of the one a .git file there
    /// points to, or <see langword="null"/> when <paramref name="path"/> is neither.
    /// </summary>
    /// <exception cref="HarnessException">git could not look.</exception>
    Task<string?> ResolveGitDirectoryAsync(string directory, string path, CancellationToken cancellationToken = default);

    /// <summary>How many commits <c>git rev-list</c> selects from <paramref name="revisions"/>.</summary>
    /// <exception cref="HarnessException">git could not walk the history.</exception>
    Task<int> CountCommitsAsync(
        string directory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What <see cref="CountCommitsAsync"/> answers, asked of the repository whose git directory is
    /// <paramref name="gitDirectory"/>, without its work tree, which may be gone: a submodule's
    /// repository after its checkout was removed, for one.
    /// </summary>
    /// <exception cref="HarnessException">git could not walk the history.</exception>
    Task<int> CountRepositoryCommitsAsync(
        string gitDirectory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default);

    /// <summary>Whether the repository whose git directory is <paramref name="gitDirectory"/> holds a stash.</summary>
    /// <exception cref="HarnessException">git could not look.</exception>
    Task<bool> HasStashAsync(string gitDirectory, CancellationToken cancellationToken = default);

    /// <summary>Whether a path is ignored by git's ignore rules.</summary>
    Task<bool> IsIgnoredAsync(string directory, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the repository at <paramref name="directory"/> ignores case in the names of its paths: its
    /// <c>core.ignorecase</c>, which git sets where the file system it was made on does, and false where it is not set.
    /// </summary>
    /// <exception cref="HarnessException">git could not read the configuration.</exception>
    Task<bool> IgnoresCaseAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of <paramref name="untracked"/> - files git status called untracked, by their paths from the top of the tree -
    /// are a file the index holds under another spelling, where the repository at <paramref name="directory"/> says by its
    /// core.ignorecase that it heeds case: there git lists a file the disk spells otherwise than its index twice, changed
    /// under the index's name and untracked under the disk's, though the directory holds one file. Decided file by file, as
    /// each directory compares names: one is that file only where every directory the two spell differently lists the
    /// untracked spelling alone and does not tell names apart by case; a new file beside a tracked one a directory tells
    /// apart, or beside a tracked one deleted, is not, nor is a name whose differing part holds a character outside ASCII,
    /// which may be composed otherwise in Unicode on disk than git gives it. Empty where the repository says it ignores
    /// case: git already finds such a name in its index. An untracked
    /// directory, named as one entry, is never among them. git's ignore rules are left as the repository has them.
    /// </summary>
    /// <exception cref="HarnessException">git could not read the configuration or the index.</exception>
    Task<IReadOnlySet<string>> IndexedInAnotherCaseAsync(string directory, IReadOnlyCollection<string> untracked, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which rule decides each of <paramref name="paths"/> in the work tree at
    /// <paramref name="directory"/>, whether or not the path exists or is tracked.
    /// </summary>
    /// <param name="directory">The work tree's root.</param>
    /// <param name="paths">Paths relative to that root, with forward separators.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <returns>
    /// One decision per path, in the order asked. A path git would not answer about - one beyond a symbolic
    /// link - where it can answer about the work tree at all, says why in <see cref="IgnoreDecision.Unanswered"/>.
    /// </returns>
    /// <exception cref="HarnessException">git could not answer about the work tree at all, or answered in another form or about other paths.</exception>
    /// <remarks>
    /// git's own answer rather than a reading of the rules: what a rule matches depends on its file's
    /// directory, on every rule before and after it, and on whether a directory above the path is
    /// already excluded, where no re-include reaches. Asked without the index, so a tracked file is
    /// judged by the rules alone. git names no re-include that matched a directory above the path, and
    /// matches a rule ending in <c>/</c> against the path itself only where it is a directory that
    /// exists - never a link to one.
    /// </remarks>
    Task<IReadOnlyList<IgnoreDecision>> ExplainIgnoredAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of <paramref name="paths"/> git ignores in the work tree at <paramref name="directory"/>, as <c>git add</c>
    /// judges them, whether or not a file is there: a file git tracks is never ignored, whatever the rules say, and a path
    /// git will not answer about - one below a submodule, or beyond a symbolic link - is one <c>git add</c> refuses too,
    /// and none it ignores.
    /// </summary>
    /// <param name="directory">The work tree's root.</param>
    /// <param name="paths">Paths relative to that root, with forward separators.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">git could not answer about the work tree at all: an unknown answer is never "nothing ignored".</exception>
    Task<IReadOnlySet<string>> FindIgnoredAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The full id of the commit <paramref name="reference"/> names, or <see langword="null"/> when it
    /// names none.
    /// </summary>
    /// <exception cref="HarnessException">git could not answer at all.</exception>
    Task<string?> ResolveCommitAsync(string directory, string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where the history of <paramref name="first"/> parts from that of <paramref name="others"/>: the full id, as
    /// <see cref="ResolveCommitAsync"/> returns one, of the best common ancestor <c>git merge-base</c> names - which is
    /// <paramref name="first"/> itself where it is an ancestor of them - or <see langword="null"/> where they share no
    /// history and the clone is known to hold all of it. Several others are read as git reads them, as the commit merging
    /// them all would be: HEAD and each commit an unfinished merge brings in, for one. In a shallow clone whose history
    /// may stop before they part, a commit another's own object names as a parent is still that answer.
    /// </summary>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="first">A commit, or a name of one, read as <see cref="ResolveCommitAsync"/> reads it.</param>
    /// <param name="others">At least one commit, or name of one, each read as <see cref="ResolveCommitAsync"/> reads it.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">
    /// git could not answer: a name that names no commit, for one, or a shallow clone whose history may stop before they
    /// part, or one that cannot say whether it holds the whole history, which are never answered as histories that share
    /// nothing.
    /// </exception>
    Task<string?> MergeBaseAsync(string directory, string first, IReadOnlyList<string> others, CancellationToken cancellationToken = default);

    /// <summary>The branch the HEAD at <paramref name="directory"/> is on, as git names it short, or <see langword="null"/> where it is on none - a detached HEAD.</summary>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git could not answer.</exception>
    Task<string?> GetBranchAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// The commits an unfinished merge in the work tree at <paramref name="directory"/> is bringing into HEAD, as its
    /// MERGE_HEAD records them, or none where no merge is in progress. The work tree then holds what they bring, as far
    /// as git could merge it.
    /// </summary>
    /// <exception cref="HarnessException">git could not say where the record is kept, or it could not be read.</exception>
    Task<IReadOnlyList<string>> ListMergeHeadsAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the clone holding <paramref name="directory"/> is shallow, its history cut short, or <see langword="null"/>
    /// where git could not say.
    /// </summary>
    Task<bool?> IsShallowAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// A file's content at <paramref name="commit"/>, or <see langword="null"/> when no file was
    /// there.
    /// </summary>
    /// <param name="directory">The repository's root, which <paramref name="relativePath"/> is relative to.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="relativePath">The file, relative to the repository root.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">
    /// git could not read the commit, or lists the file there and could not read it.
    /// </exception>
    Task<string?> ReadFileAtCommitAsync(
        string directory,
        string commit,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Each file's content at <paramref name="commit"/>, keyed by its path as given, with
    /// <see langword="null"/> for one that is not a file there. All of them are read by one git
    /// process; a path that names no file costs one listing of the commit more, which is what tells it
    /// from a file git lists and cannot read.
    /// </summary>
    /// <param name="directory">The repository's root, which the paths are relative to.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="relativePaths">The files, relative to the repository root and spelled as git spells them.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">
    /// git could not read the commit, or lists one of the files there and could not read it.
    /// </exception>
    Task<IReadOnlyDictionary<string, string?>> ReadFilesAtCommitAsync(
        string directory,
        string commit,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The files <paramref name="commit"/> holds, from the repository's root: a symbolic link is one;
    /// a directory is not, nor is a submodule's entry, which names a commit in another repository.
    /// </summary>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git could not list the commit.</exception>
    Task<IReadOnlyList<GitName>> ListFilesAtCommitAsync(
        string directory,
        string commit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The names a git command lists, in its <c>-z</c> form - separated by NUL, with nothing quoted -
    /// each as git holds it.
    /// </summary>
    /// <param name="directory">The directory git runs in.</param>
    /// <param name="arguments">A git command that lists names and nothing else, with <c>-z</c>.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git failed.</exception>
    Task<IReadOnlyList<GitName>> ListNamesAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What a copy of the tree at <paramref name="directory"/> is asked to hold so that git answers about HEAD there as
    /// it does here: the commit HEAD names - none, where it names none yet - how this repository names its objects, and
    /// where its own history stops.
    /// </summary>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="whole">Whether every commit behind HEAD is asked for, or that commit alone.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not answer.</exception>
    Task<GitHistoryWanted> DescribeHistoryAsync(string directory, bool whole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes, into the directory <paramref name="into"/>, a pack of what <paramref name="wanted"/> asks for: the commit
    /// and all its tree names, and every commit behind it where they are all asked for - less what
    /// <paramref name="leftOut"/>, a commit the other repository holds, already gives it.
    /// </summary>
    /// <param name="directory">A directory inside the repository that holds the commit.</param>
    /// <param name="wanted">What is asked for.</param>
    /// <param name="leftOut">
    /// A commit the pack is for a repository that holds - with every commit behind it where they are all asked for, and
    /// with all its tree names otherwise - or <see langword="null"/> to leave nothing out.
    /// </param>
    /// <param name="into">A directory of the caller's own, made where it is not there.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git could not write it.</exception>
    Task<GitHistoryPack> PackHistoryAsync(
        string directory,
        GitHistoryWanted wanted,
        string? leftOut,
        string into,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Where the HEAD of the repository whose top is <paramref name="directory"/> stands and, where
    /// <paramref name="wanted"/> names a commit, what of it the repository holds. Nothing is written.
    /// </summary>
    /// <param name="directory">The top of the repository's own work tree.</param>
    /// <param name="wanted">What is asked of it, or <see langword="null"/> to ask only where its HEAD stands.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not answer.</exception>
    Task<GitHistoryHeld> ReadHistoryAsync(string directory, GitHistoryWanted? wanted, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps <paramref name="piece"/>, the bytes of the pack named <paramref name="pack"/> from <paramref name="offset"/>
    /// on, aside in the repository whose top is <paramref name="directory"/>, until <see cref="TakeHistoryAsync"/> takes
    /// the pack whole. A piece at the start begins the pack again, and clears what an earlier one left unfinished.
    /// </summary>
    /// <param name="directory">The top of the repository's own work tree.</param>
    /// <param name="pack">The name git gave the pack, as <see cref="GitHistoryPack.Name"/> has it.</param>
    /// <param name="offset">How many bytes of the pack came before this piece.</param>
    /// <param name="piece">The bytes.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">
    /// The piece does not follow what was kept before it, or could not be written. No object of the repository is touched.
    /// </exception>
    Task ReceiveHistoryAsync(string directory, string pack, long offset, byte[] piece, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the HEAD of the repository whose top is <paramref name="directory"/> name the commit
    /// <paramref name="taken"/> asks for: takes the pack kept aside for it, records where the repository's history now stops,
    /// walks everything the commit names, and only then moves HEAD - itself, detached, never the branch it was on - where
    /// it names another commit. No branch, tag or commit of the repository is changed, and neither its index nor a file
    /// of its work tree: it gains the objects of the pack, and its record of where its history stops is made true of them.
    /// </summary>
    /// <param name="directory">The top of the repository's own work tree.</param>
    /// <param name="taken">The commit, how much behind it, and the pack kept aside for it.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <returns>Where HEAD was before, and whether it was moved.</returns>
    /// <exception cref="HarnessException">
    /// The pack could not be taken, or the repository does not then hold all the commit names: HEAD is left where it was,
    /// and what the repository took stays, recorded as what it is.
    /// </exception>
    /// <remarks>
    /// What was kept aside is removed either way; where it could not be, a take that succeeded says so
    /// (<see cref="GitHeadMoved.LeftAside"/>), and the next pack sent clears it or says why it cannot.
    /// </remarks>
    Task<GitHeadMoved> TakeHistoryAsync(string directory, GitHistoryTaken taken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every path of the tree at <paramref name="directory"/> whose working file is not what the index holds - changed,
    /// gone, conflicted - every path changed in the index alone, its working file the index's
    /// (<see cref="GitWorkingFile.Differs"/> false), and every untracked one git does not ignore, as <c>status --porcelain=v2
    /// -z --untracked-files=all --no-renames</c> lists them, without taking the index's lock. A path marked assume-unchanged
    /// or skip-worktree is not among them: status does not look at its file.
    /// </summary>
    /// <param name="directory">The top of the tree.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git failed, or listed what this build cannot read.</exception>
    Task<IReadOnlyList<GitWorkingFile>> ListWorkingFilesAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the working tree's status says of <paramref name="paths"/> alone, as
    /// <see cref="ListWorkingFilesAsync(string, CancellationToken)"/> says it of every path: each path named as it is spelt,
    /// never read as a pattern, and given in batches no command line outgrows.
    /// </summary>
    /// <param name="directory">The top of the tree.</param>
    /// <param name="paths">The paths, relative to the tree's top with forward separators.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git failed, or listed what this build cannot read.</exception>
    Task<IReadOnlyList<GitWorkingFile>> ListWorkingFilesAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// The blob id git would store each of <paramref name="paths"/>' working files as, its filters applied, in the same
    /// order - nothing written, no lock taken, the paths given on standard input <see cref="GitClient.HashBatch"/> at a time.
    /// </summary>
    /// <param name="directory">The top of the tree, which the paths are relative to.</param>
    /// <param name="paths">The files, each there.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git failed: a file it was given was not there, among other causes.</exception>
    Task<IReadOnlyList<string>> HashWorkingFilesAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Runs an arbitrary git subcommand, returning its exit code and output.</summary>
    Task<GitCommandResult> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput = false,
        CancellationToken cancellationToken = default);
}
