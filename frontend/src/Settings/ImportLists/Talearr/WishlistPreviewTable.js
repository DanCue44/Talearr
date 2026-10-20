import PropTypes from 'prop-types';
import React from 'react';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { kinds } from 'Helpers/Props';
import { formatNames, stateLabels } from './wishlistFormat';
import styles from './TalearrWishlists.css';

const columns = [
  { name: 'row', label: 'In the list', isVisible: true },
  { name: 'match', label: 'Matched book', isVisible: true },
  { name: 'state', label: 'What happens', isVisible: true }
];

// Talearr: rows of a wishlist with their match and what a sync does (or did) with them.
function WishlistPreviewTable({ preview }) {
  const counts = preview.items.reduce((acc, item) => {
    acc[item.state] = (acc[item.state] || 0) + 1;
    return acc;
  }, {});

  const summary = Object.keys(counts)
    .map((state) => `${counts[state]} ${(stateLabels[state] || { label: state }).label.toLowerCase()}`)
    .join(', ');

  return (
    <div>
      <div className={styles.previewSummary}>
        {`${formatNames[preview.format] || 'List'}: ${preview.rowCount == null ? preview.items.length : preview.rowCount} rows. ${summary}`}
      </div>

      <Table columns={columns}>
        <TableBody>
          {
            preview.items.map((item) => {
              const state = stateLabels[item.state] || { label: item.state, kind: kinds.DEFAULT };
              const bookId = item.audiobookBookId || item.ebookBookId;
              let matchTitle = item.bookProviderId ? item.bookTitle : '';
              if (item.bookProviderId && bookId) {
                matchTitle = <Link to={`/book/${bookId}`}>{item.bookTitle}</Link>;
              }

              return (
                <TableRow key={item.row.key}>
                  <TableRowCell>
                    <div className={styles.left}>
                      {item.row.title}
                      <span className={styles.sub}>
                        {[item.row.author, item.row.isbn, item.row.asin].filter(Boolean).join(' · ')}
                      </span>
                    </div>
                  </TableRowCell>

                  <TableRowCell>
                    <div className={styles.left}>
                      {matchTitle}
                      {
                        item.bookProviderId ?
                          <span className={styles.sub}>{[item.authorName, item.bookProviderId].filter(Boolean).join(' · ')}</span> :
                          null
                      }
                    </div>
                  </TableRowCell>

                  <TableRowCell>
                    <Label kind={state.kind}>{state.label}</Label>
                    {item.message ? <span className={styles.sub}>{item.message}</span> : null}
                  </TableRowCell>
                </TableRow>
              );
            })
          }
        </TableBody>
      </Table>
    </div>
  );
}

WishlistPreviewTable.propTypes = {
  preview: PropTypes.object.isRequired
};

export default WishlistPreviewTable;
