//
// CodeIssuePad.cs
//
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
//
// Copyright (c) 2013 Xamarin Inc. (http://xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using MonoDevelop.Ide.Gui;
using Xwt;
using MonoDevelop.Ide;
using MonoDevelop.Projects;
using System.Collections.Generic;
using ICSharpCode.NRefactory.TypeSystem;
using System.Diagnostics;

namespace MonoDevelop.CodeIssues
{
	public class CodeIssuePadControl : VBox
	{
		const int UpdatePeriod = 500;

		TreeView view = new TreeView ();
		DataField<string> textField = new DataField<string> ();
		DataField<IIssueTreeNode> nodeField = new DataField<IIssueTreeNode> ();
		Button runButton = new Button ("Run");
		Button cancelButton = new Button ("Cancel");
		CodeAnalysisBatchRunner runner = new CodeAnalysisBatchRunner();
		
		IssueGroup rootGroup;
		TreeStore store;
		
		ISet<IIssueTreeNode> syncedNodes = new HashSet<IIssueTreeNode> ();
		Dictionary<IIssueTreeNode, TreePosition> nodePositions = new Dictionary<IIssueTreeNode, TreePosition> ();

		bool runPeriodicUpdate;
		object queueLock = new object ();
		Queue<IIssueTreeNode> updateQueue = new Queue<IIssueTreeNode> ();
		
		static Type[] groupingProviders = new[] {
			typeof(CategoryGroupingProvider),
			typeof(ProviderGroupingProvider),
			typeof(SeverityGroupingProvider)
		};

		public CodeIssuePadControl ()
		{
			var buttonRow = new HBox();
			runButton.Clicked += StartAnalyzation;
			buttonRow.PackStart (runButton);
			
			cancelButton.Clicked += StopAnalyzation;
			cancelButton.Sensitive = false;
			buttonRow.PackStart (cancelButton);
			
			var groupingProvider = new CategoryGroupingProvider {
				Next = new ProviderGroupingProvider()
			};
			rootGroup = new IssueGroup (groupingProvider, "root group");
			var groupingProviderControl = new GroupingProviderChainControl (rootGroup, groupingProviders);
			buttonRow.PackStart (groupingProviderControl);
			
			PackStart (buttonRow);

			store = new TreeStore (textField, nodeField);
			view.DataSource = store;
			view.HeadersVisible = false;

			view.Columns.Add ("Name", textField);
			
			view.RowActivated += OnRowActivated;
			view.RowExpanding += OnRowExpanding;
			PackStart (view, BoxMode.FillAndExpand);
			
			IIssueTreeNode node = rootGroup;
			node.ChildrenInvalidated += (sender, group) => {
				Application.Invoke (delegate {
					ClearSiblingNodes (store.GetFirstNode ());
					store.Clear ();
					SyncStateToUi (runner.State);
					foreach(var child in ((IIssueTreeNode)rootGroup).Children) {
						var navigator = store.AddNode ();
						SetNode (navigator, child);
						SyncNode (navigator);
					}
				});
			};
			node.ChildAdded += HandleRootChildAdded;
			
			runner.IssueSink = rootGroup;
			runner.AnalysisStateChanged += HandleAnalysisStateChanged;
		}
		
		void ClearState ()
		{
			store.Clear ();
			rootGroup.ClearStatistics ();
			rootGroup.EnableProcessing ();
			
			syncedNodes.Clear ();
			nodePositions.Clear ();
			lock (queueLock) {
				updateQueue.Clear ();
			}
		}

		void HandleAnalysisStateChanged (object sender, AnalysisStateChangeEventArgs e)
		{
			Application.Invoke (delegate {
				SyncStateToUi (e.NewState);
				if (e.NewState == AnalysisState.Running) {
					StartPeriodicUpdate ();
				} else if (e.NewState == AnalysisState.Completed || e.NewState == AnalysisState.Cancelled) {
					EndPeriodicUpdate ();
				}
			});
		}

		void StartPeriodicUpdate ()
		{
			Debug.Assert (!runPeriodicUpdate);
			runPeriodicUpdate = true;
			Application.TimeoutInvoke (UpdatePeriod, RunPeriodicUpdate);
		}
		
		bool RunPeriodicUpdate ()
		{
			IList<IIssueTreeNode> nodes;
			lock (queueLock) {
				nodes = new List<IIssueTreeNode> (updateQueue);
			}
			
			foreach (var node in nodes) {
				TreePosition position;
				if (!nodePositions.TryGetValue (node, out position)) {
					// This might be an event for a group that has been invalidated and removed
					continue;
				}
				var navigator = store.GetNavigatorAt (position);
				UpdateText (navigator, node);
				if (!syncedNodes.Contains (node) && node.HasChildren) {
					if (navigator.MoveToChild ()) {
						navigator.MoveToParent ();
					} else {
						AddDummyChild (navigator);
					}
				}
			}
			
			if (runPeriodicUpdate) {
				// Add new callback
				Application.TimeoutInvoke (UpdatePeriod, RunPeriodicUpdate);
			}
			return false;
		}

		void EndPeriodicUpdate ()
		{
			runPeriodicUpdate = false;
		}

		void HandleRootChildAdded (object sender, IssueTreeNodeEventArgs e)
		{
			Application.Invoke (delegate {
				Debug.Assert (e.Parent == rootGroup);
				var navigator = store.AddNode ();
				SetNode (navigator, e.Child);
				SyncNode (navigator);
			});
		}

		void SyncStateToUi (AnalysisState state)
		{
			// Update the top row
			string text;
			switch (state) {
			case AnalysisState.Running:
				text = "Running...";
				break;
			case AnalysisState.Cancelled:
				text = string.Format ("Found issues: {0} (Cancelled)", rootGroup.IssueCount);
				break;
			case AnalysisState.Completed:
				text = string.Format ("Found issues: {0}", rootGroup.IssueCount);
				break;
			}
			if (text != null) {
				var topRow = store.GetFirstNode ();
				// Weird way to check if the store was empty during the call above.
				// Might not be portable...
				if (topRow.CurrentPosition == null) {
					topRow = store.AddNode ();
				}
				topRow.SetValue (textField, text);
			}
			
			// Set button sensitivity
			bool running = state == AnalysisState.Running;
			runButton.Sensitive = !running;
			cancelButton.Sensitive = running;
		}
		
		void StartAnalyzation (object sender, EventArgs e)
		{
			var solution = IdeApp.ProjectOperations.CurrentSelectedSolution;
			if (solution == null)
				return;
				
			ClearState ();
			
			runner.StartAnalysis (solution);
		}

		void StopAnalyzation (object sender, EventArgs e)
		{
			runner.Stop ();
		}

		void SetNode (TreeNavigator navigator, IIssueTreeNode node)
		{
			if (navigator == null)
				throw new ArgumentNullException ("navigator");
			if (node == null)
				throw new ArgumentNullException ("node");
			
			navigator.SetValue (nodeField, node);
			Debug.Assert (!nodePositions.ContainsKey (node));
			var position = navigator.CurrentPosition;
			nodePositions.Add (node, position);
			
			node.ChildAdded += (sender, e) => {
				Debug.Assert (e.Parent == node);
				Application.Invoke (delegate {
					var newNavigator = store.GetNavigatorAt (position);
					newNavigator.AddChild ();
					SetNode (newNavigator, e.Child);
					SyncNode (newNavigator);
				});
			};
			node.ChildrenInvalidated += (sender, e) => {
				Application.Invoke (delegate {
					SyncNode (store.GetNavigatorAt (position));
				});
			};
			node.TextChanged += (sender, e) => {
				lock (queueLock) {
					if (!updateQueue.Contains (e.IssueGroup)) {
						updateQueue.Enqueue (e.IssueGroup);
					}
				}
			};
		}

		void ClearSiblingNodes (TreeNavigator navigator)
		{
			do {
				var node = navigator.GetValue (nodeField);
				if (node != null) {
					if (syncedNodes.Contains (node)) {
						syncedNodes.Remove (node);
					}
					if (nodePositions.ContainsKey (node)) {
						nodePositions.Remove (node);
					}
				}
				ClearChildNodes (navigator);
			} while (navigator.MoveNext ());
		}
		
		void ClearChildNodes (TreeNavigator navigator)
		{
			if (navigator.MoveToChild ()) {
				ClearSiblingNodes (navigator);
				navigator.MoveToParent ();
			}
		}
		
		void SyncNode (TreeNavigator navigator, bool forceExpansion = false)
		{
			var node = navigator.GetValue (nodeField);
			UpdateText (navigator, node);
			bool isExpanded = forceExpansion || view.IsRowExpanded (navigator.CurrentPosition);
			ClearChildNodes (navigator);
			syncedNodes.Remove (node);
			navigator.RemoveChildren ();
			if (!node.HasChildren) 
				return;
			if (isExpanded) {
				foreach (var childNode in node.Children) {
					navigator.AddChild ();
					SetNode (navigator, childNode);
					SyncNode (navigator);
					navigator.MoveToParent ();
				}
			} else {
				AddDummyChild (navigator);
			}
			
			if (isExpanded) {
				syncedNodes.Add (node);
				view.ExpandRow (navigator.CurrentPosition, false);
			}
			
		}

		void UpdateText (TreeNavigator navigator, IIssueTreeNode node)
		{
			navigator.SetValue (textField, node.Text);
		}

		void AddDummyChild (TreeNavigator navigator)
		{
			navigator.AddChild ();
			navigator.SetValue (textField, "Loading...");
			navigator.MoveToParent ();
		}

		EventHandler<IssueGroupEventArgs> GetChildrenInvalidatedHandler (TreePosition position)
		{
			return (sender, eventArgs) => {
				Application.Invoke(delegate {
					var expanded = view.IsRowExpanded (position);
					var newNavigator = store.GetNavigatorAt (position);
					newNavigator.RemoveChildren ();
					SyncNode (newNavigator, expanded);
					if (expanded) {
						view.ExpandRow (position, false);
					}
				});
			};
		}
		
		void OnRowActivated (object sender, TreeViewRowEventArgs e)
		{
			var position = e.Position;
			var node = store.GetNavigatorAt (position).GetValue (nodeField);
			
			var issueSummary = node as IssueSummary;
			if (issueSummary != null) {
				var region = issueSummary.Region;
				IdeApp.Workbench.OpenDocument (region.FileName, region.BeginLine, region.BeginColumn);
			} else {
				if (!view.IsRowExpanded (position)) {
					view.ExpandRow (position, false);
				} else {
					view.CollapseRow (position);
				}
			}
		}

		void OnRowExpanding (object sender, TreeViewRowEventArgs e)
		{
			var navigator = store.GetNavigatorAt (e.Position);
			var node = navigator.GetValue (nodeField);
			if (!syncedNodes.Contains (node)) {
				SyncNode (navigator, true);
			}
		}
	}

	public class CodeIssuePad : AbstractPadContent
	{
		CodeIssuePadControl issueControl;

		public override Gtk.Widget Control {
			get {
				if (issueControl == null)
					issueControl = new CodeIssuePadControl ();
				return (Gtk.Widget)Xwt.Toolkit.CurrentEngine.GetNativeWidget (issueControl);
			}
		}
	}
}

